using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WebViewTrayCoordinatorTests
{
    [Fact]
    public void Start_minimized_does_not_ensure_webview()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: false);
        Assert.Equal(0, surface.EnsureCalls);
    }

    [Fact]
    public async Task Reopen_cancels_pending_blank_and_visible_state_wins_suspend_race()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        coordinator.HideToTray();
        Task suspend = surface.PendingSuspend;

        await coordinator.ShowFromTrayAsync();
        surface.CompleteSuspend(success: true);
        await suspend;
        timers.FireAll();

        Assert.True(surface.Visible);
        Assert.True(surface.Resumed);
        Assert.False(surface.BlankNavigated);
    }

    [Fact]
    public async Task Hidden_dashboard_blanks_after_one_tray_timeout()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        coordinator.HideToTray();
        surface.CompleteSuspend(success: true);
        await surface.PendingSuspend;
        Assert.Equal(1, timers.Count);
        timers.FireAll();
        Assert.Equal(1, surface.BlankCalls);
    }

    [Fact]
    public async Task Renderer_reload_is_capped_at_five_attempts()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        for (int i = 0; i < 7; i++)
            await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        Assert.Equal(5, surface.ReloadCalls);
    }

    [Fact]
    public async Task Successful_navigation_resets_renderer_retry_budget()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        for (int i = 0; i < 5; i++)
            await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        coordinator.NotifyNavigationSucceeded();
        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        Assert.Equal(6, surface.ReloadCalls);
    }

    [Fact]
    public async Task Browser_recovery_is_single_flight()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        Task first = coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);
        Task second = coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);
        Assert.Equal(1, surface.RecoverCalls);
        surface.CompleteRecovery();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public void Stop_cancels_pending_tray_teardown()
    {
        var surface = new FakeDashboardSurface();
        var timers = new ManualTimerFactory();
        using var coordinator = new WebViewTrayCoordinator(surface, timers.Create, TimeSpan.FromSeconds(20));
        coordinator.Start(initiallyVisible: true);
        coordinator.HideToTray();
        coordinator.Stop();
        timers.FireAll();
        Assert.Equal(0, surface.BlankCalls);
        Assert.Equal(0, timers.Count);
    }

    [Fact]
    public void Webview_binding_never_duplicates_handlers_for_same_core()
    {
        var first = new object();
        var second = new object();
        int attaches = 0, detaches = 0;
        using var binding = new WebViewLifecycleBinding<object>(_ => attaches++, _ => detaches++);
        binding.Attach(first);
        binding.Attach(first);
        Assert.Equal(1, attaches);
        Assert.Equal(0, detaches);
        binding.Attach(second);
        Assert.Equal(2, attaches);
        Assert.Equal(1, detaches);
        binding.Dispose();
        binding.Dispose();
        Assert.Equal(2, detaches);
    }

    private sealed class ManualTimerFactory
    {
        private readonly List<Action> _callbacks = new();
        public int Count => _callbacks.Count;
        public IDisposable Create(TimeSpan _, Action callback)
        {
            _callbacks.Add(callback);
            return new CallbackDisposable(onDispose: () => _callbacks.Remove(callback));
        }
        public void FireAll()
        {
            foreach (Action callback in _callbacks.ToArray()) callback();
        }
    }

    private sealed class FakeDashboardSurface : IDashboardSurface
    {
        private readonly TaskCompletionSource<bool> _suspend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _recovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsVisible => Visible;
        public bool Visible { get; private set; } = true;
        public bool Resumed { get; private set; }
        public bool BlankNavigated { get; private set; }
        public int BlankCalls { get; private set; }
        public int ReloadCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public int EnsureCalls { get; private set; }
        public Task PendingSuspend => _suspend.Task;
        public void HideWindow() => Visible = false;
        public void ShowAndActivateWindow() => Visible = true;
        public Task EnsureWebViewAsync(CancellationToken _) { EnsureCalls++; return Task.CompletedTask; }
        public void SetWebViewVisible(bool visible) => Visible = visible;
        public Task<bool> SuspendAsync(CancellationToken _) => _suspend.Task;
        public void Resume() => Resumed = true;
        public void NavigateBlank() { BlankNavigated = true; BlankCalls++; }
        public void NavigateApp() { }
        public void Reload() => ReloadCalls++;
        public Task RecoverBrowserAsync(CancellationToken _) { RecoverCalls++; return _recovery.Task; }
        public void PublishFreshState() { }
        public void CompleteSuspend(bool success) => _suspend.TrySetResult(success);
        public void CompleteRecovery() => _recovery.TrySetResult(true);
    }
}
