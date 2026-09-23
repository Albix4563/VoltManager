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
    BrowserProcessExited,
}

internal sealed class WebViewTrayCoordinator : IDisposable
{
    private const int RendererRetryLimit = 5;
    private readonly object _gate = new();
    private readonly IDashboardSurface _surface;
    private readonly RestartableLifecycle _lifecycle = new();
    private Task? _recoveryTask;
    private Task? _showTask;
    private Task? _suspendTask;
    private int _rendererReloadCount;
    private bool _activateWindowAfterRestore;
    private bool _visible;
    private bool _disposed;

    public WebViewTrayCoordinator(IDashboardSurface surface)
        => _surface = surface;

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
        lock (_gate) _visible = false;
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
            if (_showTask is { IsCompleted: false })
            {
                _activateWindowAfterRestore |= activateWindow;
                return _showTask;
            }

            _visible = true;
            _activateWindowAfterRestore = activateWindow;
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

        if (kind == WebViewFailureKind.Renderer)
        {
            if (Interlocked.Increment(ref _rendererReloadCount) <= RendererRetryLimit && _lifecycle.IsCurrent(epoch))
            {
                _surface.ShowLoading();
                _surface.Reload();
            }
            else if (_lifecycle.IsCurrent(epoch))
            {
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

    public void NotifyNavigationSucceeded()
        => Interlocked.Exchange(ref _rendererReloadCount, 0);

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
            await _surface.RecoverBrowserAsync(epoch).ConfigureAwait(false);
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
