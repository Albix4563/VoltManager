using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WebViewTrayCoordinatorTests
{
    [Fact]
    public void Start_minimized_does_not_ensure_webview()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);
        Assert.Equal(0, surface.EnsureCalls);
    }

    [Fact]
    public void Start_visible_resumes_before_making_webview_visible()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);

        coordinator.Start(initiallyVisible: true);

        int resumeIndex = surface.Events.IndexOf("resume");
        int visibleIndex = surface.Events.IndexOf("webview:visible");
        Assert.True(resumeIndex >= 0);
        Assert.True(visibleIndex > resumeIndex);
    }

    [Fact]
    public void Window_visibility_restore_does_not_activate_the_window()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        coordinator.SetVisible(true);

        Assert.Equal(0, surface.ShowCalls);
        Assert.Equal(1, surface.ResumeCalls);
    }

    [Fact]
    public async Task Reopen_waits_for_pending_suspend_then_resumes_once()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        int resumesBeforeHide = surface.ResumeCalls;
        coordinator.HideToTray();
        Task suspend = surface.PendingSuspend;

        Task reopen = coordinator.ShowFromTrayAsync();
        await Task.Yield();

        Assert.Equal(resumesBeforeHide, surface.ResumeCalls);
        surface.CompleteSuspend(success: true);
        await Task.WhenAll(suspend, reopen);

        Assert.True(surface.Visible);
        Assert.Equal(resumesBeforeHide + 1, surface.ResumeCalls);
    }

    [Fact]
    public async Task Reopen_during_suspend_entry_still_waits_for_suspend_completion()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        int resumesBeforeHide = surface.ResumeCalls;
        Task? reopen = null;
        surface.OnSuspendStarted = () => reopen = coordinator.ShowFromTrayAsync();

        coordinator.HideToTray();
        await Task.Yield();

        Assert.NotNull(reopen);
        Assert.Equal(resumesBeforeHide, surface.ResumeCalls);

        surface.CompleteSuspend(success: true);
        await reopen!;
        Assert.Equal(resumesBeforeHide + 1, surface.ResumeCalls);
    }

    [Fact]
    public async Task Hidden_dashboard_remains_loaded_while_suspended()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        int navigationsBeforeHide = surface.NavigateAppCalls;
        coordinator.HideToTray();
        surface.CompleteSuspend(success: true);
        await surface.PendingSuspend;
        Assert.Equal(navigationsBeforeHide, surface.NavigateAppCalls);
    }

    [Fact]
    public async Task Repeated_reopen_clicks_share_one_restore()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        int ensuresBeforeHide = surface.EnsureCalls;
        int resumesBeforeHide = surface.ResumeCalls;
        coordinator.HideToTray();

        Task first = coordinator.ShowFromTrayAsync();
        Task second = coordinator.ShowFromTrayAsync();
        await Task.Yield();

        Assert.Equal(ensuresBeforeHide, surface.EnsureCalls);
        Assert.Equal(resumesBeforeHide, surface.ResumeCalls);

        surface.CompleteSuspend(success: true);
        await Task.WhenAll(first, second);

        Assert.Equal(ensuresBeforeHide + 1, surface.EnsureCalls);
        Assert.Equal(resumesBeforeHide + 1, surface.ResumeCalls);
        Assert.Equal(1, surface.ShowCalls);
    }

    [Fact]
    public async Task Reopen_after_hide_during_pending_open_still_restores_window()
    {
        var surface = new FakeDashboardSurface { HoldEnsure = true };
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        Task firstOpen = coordinator.ShowFromTrayAsync();
        coordinator.HideToTray();
        Task secondOpen = coordinator.ShowFromTrayAsync();
        surface.CompleteEnsure();
        surface.CompleteSuspend(success: false);
        await Task.WhenAll(firstOpen, secondOpen);

        Assert.True(surface.Visible);
        Assert.True(surface.ShowCalls > 0);
    }

    [Fact]
    public void Repeated_hide_requests_start_only_one_suspend()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);

        coordinator.SetVisible(false);
        coordinator.HideToTray();

        Assert.Equal(1, surface.SuspendCalls);
    }

    [Fact]
    public async Task Hiding_during_open_discards_its_pending_activation()
    {
        var surface = new FakeDashboardSurface { HoldEnsure = true };
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        Task firstOpen = coordinator.ShowFromTrayAsync();
        coordinator.HideToTray();
        coordinator.SetVisible(true);
        surface.CompleteEnsure();
        surface.CompleteSuspend(success: false);
        await firstOpen;

        Assert.Equal(0, surface.ShowCalls);
        Assert.True(surface.Visible);
    }

    [Fact]
    public async Task Reopen_resumes_webview_before_making_it_visible()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        coordinator.HideToTray();
        surface.CompleteSuspend(success: true);
        await surface.PendingSuspend;
        surface.Events.Clear();

        await coordinator.ShowFromTrayAsync();

        int resumeIndex = surface.Events.IndexOf("resume");
        int visibleIndex = surface.Events.IndexOf("webview:visible");
        Assert.True(resumeIndex >= 0, "Restore must resume WebView2.");
        Assert.True(visibleIndex > resumeIndex, "WebView2 must be resumed before it becomes visible.");
    }

    [Fact]
    public async Task Renderer_reload_is_capped_at_five_attempts()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        for (int i = 0; i < 7; i++)
            await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
        Assert.Equal(5, surface.ReloadCalls);
        Assert.Equal(5, surface.LoadingCalls);
        Assert.Equal(2, surface.ErrorCalls);
    }

    [Fact]
    public async Task Successful_navigation_resets_renderer_retry_budget()
    {
        var surface = new FakeDashboardSurface();
        using var coordinator = new WebViewTrayCoordinator(surface);
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
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: true);
        Task first = coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);
        Task second = coordinator.HandleProcessFailureAsync(WebViewFailureKind.BrowserProcessExited);
        Assert.Equal(1, surface.RecoverCalls);
        surface.CompleteRecovery();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Failed_restore_shows_retryable_error_state()
    {
        var surface = new FakeDashboardSurface { FailEnsure = true };
        using var coordinator = new WebViewTrayCoordinator(surface);
        coordinator.Start(initiallyVisible: false);

        await coordinator.ShowFromTrayAsync();

        Assert.Equal(1, surface.ErrorCalls);
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

    private sealed class FakeDashboardSurface : IDashboardSurface
    {
        private readonly TaskCompletionSource<bool> _ensure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _suspend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _recovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsVisible => Visible;
        public bool Visible { get; private set; } = true;
        public bool Resumed { get; private set; }
        public int ResumeCalls { get; private set; }
        public int NavigateAppCalls { get; private set; }
        public int ReloadCalls { get; private set; }
        public int LoadingCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public int EnsureCalls { get; private set; }
        public int SuspendCalls { get; private set; }
        public int ShowCalls { get; private set; }
        public bool FailEnsure { get; set; }
        public bool HoldEnsure { get; set; }
        public List<string> Events { get; } = new();
        public Action? OnSuspendStarted { get; set; }
        public Task PendingSuspend => _suspend.Task;
        public void HideWindow() => Visible = false;
        public void ShowAndActivateWindow() { Visible = true; ShowCalls++; }
        public Task EnsureWebViewAsync(CancellationToken _)
        {
            EnsureCalls++;
            if (FailEnsure) throw new InvalidOperationException("webview initialization failed");
            return HoldEnsure ? _ensure.Task : Task.CompletedTask;
        }
        public void SetWebViewVisible(bool visible)
        {
            Events.Add(visible ? "webview:visible" : "webview:hidden");
            Visible = visible;
        }
        public Task<bool> SuspendAsync(CancellationToken _)
        {
            SuspendCalls++;
            OnSuspendStarted?.Invoke();
            return _suspend.Task;
        }
        public void Resume()
        {
            Events.Add("resume");
            Resumed = true;
            ResumeCalls++;
        }
        public void NavigateApp() => NavigateAppCalls++;
        public void Reload() => ReloadCalls++;
        public void ShowLoading() => LoadingCalls++;
        public void ShowLoadError() => ErrorCalls++;
        public Task RecoverBrowserAsync(CancellationToken _) { RecoverCalls++; return _recovery.Task; }
        public void PublishFreshState() { }
        public void CompleteSuspend(bool success) => _suspend.TrySetResult(success);
        public void CompleteEnsure() => _ensure.TrySetResult(true);
        public void CompleteRecovery() => _recovery.TrySetResult(true);
    }
}
