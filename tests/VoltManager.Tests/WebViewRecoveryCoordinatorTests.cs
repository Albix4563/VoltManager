using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WebViewRecoveryCoordinatorTests
{
    [Fact]
    public async Task Unresponsive_failure_storm_reloads_dashboard_only_once()
    {
        var surface = new RecoverySurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        Task[] failures = Enumerable.Range(0, 64)
            .Select(_ => coordinator.HandleProcessFailureAsync(WebViewFailureKind.RendererUnresponsive))
            .ToArray();
        await Task.WhenAll(failures);

        Assert.Equal(1, surface.ReloadCalls);
        Assert.Equal(1, surface.LoadingCalls);
    }

    [Fact]
    public async Task Browser_recovery_retries_with_bounded_backoff()
    {
        var surface = new RecoverySurface();
        int attempts = 0;
        surface.Recover = _ =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("synthetic failure");
            return Task.CompletedTask;
        };
        var delays = new List<TimeSpan>();
        using var coordinator = new WebViewTrayCoordinator(surface, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        coordinator.Start(initiallyVisible: false);

        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)], delays);
        Assert.Equal(0, surface.ErrorCalls);
    }

    [Fact]
    public async Task Concurrent_browser_failures_share_one_recovery()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new RecoverySurface { Recover = _ => pending.Task };
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        Task[] recoveries = Enumerable.Range(0, 64)
            .Select(_ => coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited))
            .ToArray();

        Assert.Equal(1, surface.RecoverCalls);
        Assert.All(recoveries, task => Assert.Same(recoveries[0], task));
        pending.SetResult(true);
        await Task.WhenAll(recoveries);
    }

    [Fact]
    public async Task Lifecycle_stop_cancels_browser_recovery_backoff()
    {
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new RecoverySurface
        {
            Recover = _ => throw new InvalidOperationException("force retry"),
        };
        using var coordinator = new WebViewTrayCoordinator(surface, (_, cancellationToken) =>
        {
            delayEntered.TrySetResult(true);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        coordinator.Start(initiallyVisible: false);

        Task recovery = coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);
        await delayEntered.Task;
        coordinator.Stop();
        await recovery;

        Assert.Equal(1, surface.RecoverCalls);
        Assert.Equal(0, surface.ErrorCalls);
    }

    [Fact]
    public async Task Successful_navigation_still_resets_renderer_cap()
    {
        var surface = new RecoverySurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        for (int i = 0; i < 5; i++)
            await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        Assert.Equal(5, surface.ReloadCalls);

        coordinator.NotifyNavigationSucceeded();
        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        Assert.Equal(6, surface.ReloadCalls);
    }

    [Fact]
    public async Task Crash_reload_loop_with_successful_navigations_stays_capped()
    {
        var surface = new RecoverySurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        for (int i = 0; i < 50; i++)
        {
            await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
            if (surface.ReloadCalls == i + 1)
                coordinator.NotifyNavigationSucceeded();
        }

        Assert.Equal(5, surface.ReloadCalls);
    }

    private sealed class RecoverySurface : IDashboardSurface
    {
        private Func<CancellationToken, Task> _recover = _ => Task.CompletedTask;

        public Func<CancellationToken, Task> Recover
        {
            get => _recover;
            set => _recover = value;
        }

        public bool IsVisible => false;
        public int ReloadCalls { get; private set; }
        public int LoadingCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public void HideWindow() { }
        public void ShowAndActivateWindow() { }
        public Task EnsureWebViewAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetWebViewVisible(bool visible) { }
        public Task<bool> SuspendAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Resume() { }
        public void NavigateApp() { }
        public void Reload() => ReloadCalls++;
        public void ShowLoading() => LoadingCalls++;
        public void ShowLoadError() => ErrorCalls++;
        public void PublishFreshState() { }
        public Task RecoverBrowserAsync(CancellationToken cancellationToken)
        {
            RecoverCalls++;
            return _recover(cancellationToken);
        }
    }
}
