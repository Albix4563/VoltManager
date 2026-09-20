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
    void NavigateBlank();
    void NavigateApp();
    void Reload();
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
    private readonly Func<TimeSpan, Action, IDisposable> _createTimer;
    private readonly TimeSpan _trayTeardownDelay;
    private readonly RestartableLifecycle _lifecycle = new();
    private IDisposable? _trayTimer;
    private Task? _recoveryTask;
    private int _rendererReloadCount;
    private int _suspendGeneration;
    private bool _visible;
    private bool _disposed;

    public WebViewTrayCoordinator(
        IDashboardSurface surface,
        Func<TimeSpan, Action, IDisposable> createTimer,
        TimeSpan trayTeardownDelay)
    {
        _surface = surface;
        _createTimer = createTimer;
        _trayTeardownDelay = trayTeardownDelay;
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

        _surface.SetWebViewVisible(initiallyVisible);
        if (initiallyVisible)
            _ = EnsureAndResumeAsync(epoch);
    }

    public void HideToTray()
    {
        if (!TryCurrentEpoch(out CancellationToken epoch)) return;
        lock (_gate) _visible = false;
        _surface.HideWindow();
        _surface.SetWebViewVisible(false);
        _ = SuspendHiddenAsync(epoch);
        ScheduleTrayTeardown(epoch);
    }

    public async Task ShowFromTrayAsync()
    {
        if (!TryCurrentEpoch(out CancellationToken epoch)) return;
        lock (_gate) _visible = true;
        CancelTrayTeardown();
        _surface.ShowAndActivateWindow();
        _surface.SetWebViewVisible(true);
        await _surface.EnsureWebViewAsync(epoch).ConfigureAwait(false);
        if (!_lifecycle.IsCurrent(epoch) || !IsVisible()) return;
        Interlocked.Increment(ref _suspendGeneration);
        _surface.Resume();
        _surface.NavigateApp();
        _surface.PublishFreshState();
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

        _surface.SetWebViewVisible(visible);
        if (!visible)
        {
            _ = SuspendHiddenAsync(epoch);
            return;
        }

        CancelTrayTeardown();
        Interlocked.Increment(ref _suspendGeneration);
        _surface.Resume();
        _surface.NavigateApp();
        _surface.PublishFreshState();
    }

    public Task HandleProcessFailureAsync(WebViewFailureKind kind)
    {
        if (!TryCurrentEpoch(out CancellationToken epoch))
            return Task.CompletedTask;

        if (kind == WebViewFailureKind.Renderer)
        {
            if (Interlocked.Increment(ref _rendererReloadCount) <= RendererRetryLimit && _lifecycle.IsCurrent(epoch))
                _surface.Reload();
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_recoveryTask is { IsCompleted: false })
                return _recoveryTask;
            _recoveryTask = RecoverBrowserAsync(epoch);
            return _recoveryTask;
        }
    }

    public void NotifyNavigationSucceeded()
        => Interlocked.Exchange(ref _rendererReloadCount, 0);

    public void Stop()
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (!_lifecycle.Stop()) return;
            timer = _trayTimer;
            _trayTimer = null;
            _recoveryTask = null;
            _visible = false;
        }
        timer?.Dispose();
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
            _surface.NavigateApp();
            _surface.PublishFreshState();
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("WebView visible-start failed", ex);
        }
    }

    private async Task SuspendHiddenAsync(CancellationToken epoch)
    {
        int generation = Interlocked.Increment(ref _suspendGeneration);
        try
        {
            bool suspended = await _surface.SuspendAsync(epoch).ConfigureAwait(false);
            if (!suspended && _lifecycle.IsCurrent(epoch) && !IsVisible())
                Logger.Info("WebView2 declined suspension for the dashboard.");

            if (_lifecycle.IsCurrent(epoch) &&
                (IsVisible() || generation != Volatile.Read(ref _suspendGeneration) && IsVisible()))
                _surface.Resume();
        }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn("WebView TrySuspend failed: " + ex.Message);
        }
    }

    private void ScheduleTrayTeardown(CancellationToken epoch)
    {
        CancelTrayTeardown();
        IDisposable timer = _createTimer(_trayTeardownDelay, () =>
        {
            if (!_lifecycle.IsCurrent(epoch) || IsVisible()) return;
            try
            {
                _surface.NavigateBlank();
                _ = SuspendHiddenAsync(epoch);
                Logger.Info("WebView blanked after tray park.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Tray WebView teardown failed: " + ex.Message);
            }
        });

        lock (_gate)
        {
            if (!_lifecycle.IsCurrent(epoch) || _visible)
            {
                timer.Dispose();
                return;
            }
            _trayTimer = timer;
        }
    }

    private void CancelTrayTeardown()
    {
        IDisposable? timer;
        lock (_gate)
        {
            timer = _trayTimer;
            _trayTimer = null;
        }
        timer?.Dispose();
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
