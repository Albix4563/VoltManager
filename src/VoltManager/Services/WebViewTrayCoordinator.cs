namespace VoltManager.Services;

internal interface IDashboardSurface
{
    bool IsVisible { get; }
    void HideWindow();
    void ShowAndActivateWindow();
    Task EnsureWebViewAsync(CancellationToken cancellationToken);
    void SetWebViewVisible(bool visible);
    Task<bool> SuspendAsync(CancellationToken cancellationToken);
    void Resume();
    void NavigateApp();
    void Reload();
    void ShowLoading();
    void ShowLoadError();
    Task RecoverBrowserAsync(CancellationToken cancellationToken);
    void PublishFreshState();
}

internal enum WebViewFailureKind
{
    Renderer,
    RendererUnresponsive,
    BrowserProcessExited,
}

internal sealed class WebViewTrayCoordinator : IDisposable
{
    private static readonly TimeSpan[] BrowserRecoveryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
    ];
    private readonly object _gate = new();
    private readonly IDashboardSurface _surface;
    private readonly Func<TimeSpan, CancellationToken, Task> _recoveryDelay;
    private readonly RestartableLifecycle _lifecycle = new();
    private Task? _recoveryTask;
    private Task? _showTask;
    private Task? _suspendTask;
    // Crash-reload loops stay bounded even when each reload navigates successfully.
    private readonly WebViewFailureBudget _rendererBudget = new();
    private int _rendererBudgetExhausted;
    private int _unresponsiveReloadUsed;
    private bool _activateWindowAfterRestore;
    private bool _visible;
    private bool _disposed;

    public WebViewTrayCoordinator(
        IDashboardSurface surface,
        Func<TimeSpan, CancellationToken, Task>? recoveryDelay = null)
    {
        _surface = surface;
        _recoveryDelay = recoveryDelay ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    public void Start(bool initiallyVisible)
    {
        CancellationToken epoch;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebViewTrayCoordinator));
            if (_lifecycle.IsStarted)
                return;
            epoch = _lifecycle.Start();
            _visible = initiallyVisible;
        }

        _surface.SetWebViewVisible(false);
        if (initiallyVisible)
            _ = EnsureAndResumeAsync(epoch);
    }

    public void HideToTray()
    {
        if (!TryCurrentEpoch(out CancellationToken epoch)) return;
        lock (_gate)
        {
            _visible = false;
            _activateWindowAfterRestore = false;
        }
        _surface.HideWindow();
        _surface.SetWebViewVisible(false);
        StartSuspend(epoch);
    }

    public Task ShowFromTrayAsync(bool activateWindow = true)
    {
        if (!TryCurrentEpoch(out CancellationToken epoch))
            return Task.CompletedTask;

        lock (_gate)
        {
            _visible = true;
            _activateWindowAfterRestore |= activateWindow;
            if (_showTask is { IsCompleted: false })
                return _showTask;

            Task pendingSuspend = _suspendTask ?? Task.CompletedTask;
            _showTask = RestoreVisibleAsync(epoch, pendingSuspend);
            return _showTask;
        }
    }

    public void SetVisible(bool visible)
    {
        if (!TryCurrentEpoch(out CancellationToken epoch)) return;
        bool changed;
        lock (_gate)
        {
            changed = _visible != visible;
            _visible = visible;
            if (!visible) _activateWindowAfterRestore = false;
        }
        if (!changed) return;

        if (!visible)
        {
            _surface.SetWebViewVisible(false);
            StartSuspend(epoch);
            return;
        }

        _surface.SetWebViewVisible(false);
        _ = ShowFromTrayAsync(activateWindow: false);
    }

    public Task HandleProcessFailureAsync(WebViewFailureKind kind)
    {
        if (!TryCurrentEpoch(out CancellationToken epoch))
            return Task.CompletedTask;

        if (kind is WebViewFailureKind.Renderer or WebViewFailureKind.RendererUnresponsive)
        {
            if (kind == WebViewFailureKind.RendererUnresponsive &&
                Interlocked.Exchange(ref _unresponsiveReloadUsed, 1) != 0)
                return Task.CompletedTask;

            if (_rendererBudget.TryTakeRendererReload())
            {
                if (_lifecycle.IsCurrent(epoch))
                {
                    _surface.ShowLoading();
                    _surface.Reload();
                }
            }
            else
            {
                Volatile.Write(ref _rendererBudgetExhausted, 1);
                if (_lifecycle.IsCurrent(epoch))
                    _surface.ShowLoadError();
            }
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_recoveryTask is { IsCompleted: false })
                return _recoveryTask;
            _surface.ShowLoading();
            _recoveryTask = RecoverBrowserAsync(epoch);
            return _recoveryTask;
        }
    }

    /// <summary>
    /// A successful navigation after the budget ran out is a manual retry from the error
    /// page: grant a fresh budget. Automatic reloads never refill it.
    /// </summary>
    public void NotifyNavigationSucceeded()
    {
        if (Interlocked.Exchange(ref _rendererBudgetExhausted, 0) != 0)
            _rendererBudget.ResetRendererReloadBudget();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_lifecycle.Stop()) return;
            _recoveryTask = null;
            _showTask = null;
            _suspendTask = null;
            _visible = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _lifecycle.Dispose();
    }

    private async Task EnsureAndResumeAsync(CancellationToken epoch)
    {
        try
        {
            await _surface.EnsureWebViewAsync(epoch).ConfigureAwait(false);
            if (!_lifecycle.IsCurrent(epoch) || !IsVisible()) return;
            _surface.Resume();
            _surface.SetWebViewVisible(true);
            _surface.NavigateApp();
            _surface.PublishFreshState();
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("WebView visible-start failed", ex);
            if (_lifecycle.IsCurrent(epoch) && IsVisible())
                _surface.ShowLoadError();
        }
    }

    private async Task SuspendHiddenAsync(CancellationToken epoch)
    {
        try
        {
            bool suspended = await _surface.SuspendAsync(epoch).ConfigureAwait(false);
            if (!suspended && _lifecycle.IsCurrent(epoch) && !IsVisible())
                Logger.Info("WebView2 declined suspension for the dashboard.");
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn("WebView TrySuspend failed: " + ex.Message);
        }
    }

    private void StartSuspend(CancellationToken epoch)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_lifecycle.IsCurrent(epoch) || _visible)
            {
                completion.TrySetResult(true);
                return;
            }

            if (_suspendTask is { IsCompleted: false })
                return;

            _suspendTask = completion.Task;
        }

        _ = CompleteSuspendAsync(epoch, completion);
    }

    private async Task CompleteSuspendAsync(
        CancellationToken epoch,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await SuspendHiddenAsync(epoch).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private async Task RestoreVisibleAsync(CancellationToken epoch, Task pendingSuspend)
    {
        try
        {
            await pendingSuspend.ConfigureAwait(false);
            if (!_lifecycle.IsCurrent(epoch) || !IsVisible()) return;

            await _surface.EnsureWebViewAsync(epoch).ConfigureAwait(false);
            if (!_lifecycle.IsCurrent(epoch) || !IsVisible()) return;

            _surface.Resume();
            _surface.SetWebViewVisible(true);
            bool activateWindow;
            lock (_gate)
            {
                activateWindow = _activateWindowAfterRestore;
                _activateWindowAfterRestore = false;
            }
            if (activateWindow)
                _surface.ShowAndActivateWindow();
            _surface.NavigateApp();
            _surface.PublishFreshState();
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("WebView restore failed", ex);
            if (_lifecycle.IsCurrent(epoch) && IsVisible())
                _surface.ShowLoadError();
        }
    }

    private async Task RecoverBrowserAsync(CancellationToken epoch)
    {
        try
        {
            Exception? lastFailure = null;
            for (int attempt = 0; attempt < BrowserRecoveryDelays.Length; attempt++)
            {
                try
                {
                    TimeSpan delay = BrowserRecoveryDelays[attempt];
                    if (delay > TimeSpan.Zero)
                        await _recoveryDelay(delay, epoch).ConfigureAwait(false);
                    epoch.ThrowIfCancellationRequested();
                    await _surface.RecoverBrowserAsync(epoch).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (epoch.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    if (attempt + 1 < BrowserRecoveryDelays.Length)
                        Logger.Warn($"WebView browser recovery attempt {attempt + 1} failed: {ex.Message}");
                }
            }

            throw new InvalidOperationException("WebView browser recovery retry limit reached.", lastFailure);
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("WebView re-init after browser exit failed", ex);
            if (_lifecycle.IsCurrent(epoch) && IsVisible())
                _surface.ShowLoadError();
        }
        finally
        {
            lock (_gate)
            {
                if (_recoveryTask?.IsCompleted != false)
                    _recoveryTask = null;
            }
        }
    }

    private bool IsVisible()
    {
        lock (_gate) return _visible;
    }

    private bool TryCurrentEpoch(out CancellationToken epoch)
    {
        lock (_gate)
        {
            if (!_lifecycle.IsStarted)
            {
                epoch = default;
                return false;
            }
            epoch = _lifecycle.Start();
            return true;
        }
    }
}
