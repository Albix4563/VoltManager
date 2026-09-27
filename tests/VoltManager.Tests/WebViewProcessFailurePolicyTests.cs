using Microsoft.Web.WebView2.Core;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WebViewProcessFailurePolicyTests
{
    public static TheoryData<CoreWebView2ProcessFailedKind, int> Classifications => new()
    {
        { CoreWebView2ProcessFailedKind.BrowserProcessExited, (int)WebViewProcessFailureAction.RecoverBrowser },
        { CoreWebView2ProcessFailedKind.RenderProcessExited, (int)WebViewProcessFailureAction.ReloadRenderer },
        { CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, (int)WebViewProcessFailureAction.ReloadRendererOnce },
        { CoreWebView2ProcessFailedKind.FrameRenderProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.UtilityProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.GpuProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, (int)WebViewProcessFailureAction.LogOnly },
        { CoreWebView2ProcessFailedKind.UnknownProcessExited, (int)WebViewProcessFailureAction.LogOnly },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Every_process_kind_has_the_expected_action(
        CoreWebView2ProcessFailedKind kind,
        int expected)
    {
        foreach (CoreWebView2ProcessFailedReason reason in Enum.GetValues<CoreWebView2ProcessFailedReason>())
            Assert.Equal(expected, (int)WebViewProcessFailureClassifier.Classify(kind, reason));
    }

    [Fact]
    public void Unknown_future_kind_is_log_only()
        => Assert.Equal(
            WebViewProcessFailureAction.LogOnly,
            WebViewProcessFailureClassifier.Classify(
                (CoreWebView2ProcessFailedKind)int.MaxValue,
                CoreWebView2ProcessFailedReason.Unexpected));

    [Fact]
    public void Unresponsive_storm_consumes_one_reload_even_under_concurrency()
    {
        var budget = new WebViewFailureBudget();
        int accepted = 0;

        Parallel.For(0, 128, _ =>
        {
            if (budget.TryTakeUnresponsiveReload())
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Renderer_budget_preserves_five_reload_cap_and_time_window_reset()
    {
        long now = 1_000;
        var budget = new WebViewFailureBudget(nowMilliseconds: () => now);

        for (int i = 0; i < 5; i++)
            Assert.True(budget.TryTakeRendererReload());
        Assert.False(budget.TryTakeRendererReload());

        now += (long)TimeSpan.FromMinutes(2).TotalMilliseconds + 1;
        Assert.True(budget.TryTakeRendererReload());
        budget.ResetRendererReloadBudget();
        Assert.True(budget.TryTakeRendererReload());
    }

    [Fact]
    public void Gpu_sliding_window_reports_only_on_threshold_crossing_and_rearms()
    {
        DateTimeOffset now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var window = new SlidingFailureWindow(3, TimeSpan.FromMinutes(10), () => now);

        Assert.Equal(new FailureWindowObservation(1, false), window.Record());
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(new FailureWindowObservation(2, false), window.Record());
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(new FailureWindowObservation(3, true), window.Record());
        Assert.Equal(new FailureWindowObservation(4, false), window.Record());

        now += TimeSpan.FromMinutes(11);
        Assert.Equal(new FailureWindowObservation(1, false), window.Record());
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(new FailureWindowObservation(2, false), window.Record());
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(new FailureWindowObservation(3, true), window.Record());
    }

    [Fact]
    public void Software_fallback_request_is_idempotent()
    {
        WebViewSoftwareRendererFallback.ResetForTests();
        try
        {
            Assert.False(WebViewSoftwareRendererFallback.IsRequested);
            Assert.True(WebViewSoftwareRendererFallback.Request());
            Assert.True(WebViewSoftwareRendererFallback.IsRequested);
            Assert.False(WebViewSoftwareRendererFallback.Request());
        }
        finally
        {
            WebViewSoftwareRendererFallback.ResetForTests();
        }
    }

    [Fact]
    public void Shared_environment_provider_is_single_flight_for_initial_and_replacement_generation()
    {
        int factoryCalls = 0;
        var provider = new SharedAsyncResourceProvider<object>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(new object());
        });

        Task<object>[] initial = Enumerable.Range(0, 64).Select(_ => provider.GetCurrent()).ToArray();
        Assert.All(initial, task => Assert.Same(initial[0], task));
        Assert.Equal(1, factoryCalls);

        Task<object>[] replacement = Enumerable.Range(0, 64)
            .Select(_ => provider.ReplaceAfterFailure(initial[0]))
            .ToArray();
        Assert.All(replacement, task => Assert.Same(replacement[0], task));
        Assert.NotSame(initial[0], replacement[0]);
        Assert.Equal(2, factoryCalls);

        Assert.Same(replacement[0], provider.ReplaceAfterFailure(initial[0]));
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void Retry_after_failed_attempt_reuses_healthy_replacement()
    {
        int factoryCalls = 0;
        var provider = new SharedAsyncResourceProvider<object>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(new object());
        });
        Task<object> crashed = provider.GetCurrent();

        // Dashboard and widget both report the same crashed environment, then retry with it.
        Task<object> dashboard = provider.ReplaceAfterFailure(crashed);
        Task<object> widget = provider.ReplaceAfterFailure(crashed);
        Task<object> dashboardRetry = provider.ReplaceAfterFailure(crashed);

        Assert.Same(dashboard, widget);
        Assert.Same(dashboard, dashboardRetry);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void Faulted_replacement_is_recreated_on_retry()
    {
        int factoryCalls = 0;
        var provider = new SharedAsyncResourceProvider<object>(() =>
            Interlocked.Increment(ref factoryCalls) == 2
                ? Task.FromException<object>(new InvalidOperationException("creation failed"))
                : Task.FromResult(new object()));
        Task<object> crashed = provider.GetCurrent();

        Task<object> failedReplacement = provider.ReplaceAfterFailure(crashed);
        Assert.True(failedReplacement.IsFaulted);
        Task<object> retry = provider.ReplaceAfterFailure(crashed);

        Assert.NotSame(failedReplacement, retry);
        Assert.True(retry.IsCompletedSuccessfully);
        Assert.Equal(3, factoryCalls);
    }

    [Fact]
    public void Peek_never_creates_the_environment_and_tracks_replacement()
    {
        int factoryCalls = 0;
        var provider = new SharedAsyncResourceProvider<object>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(new object());
        });

        Assert.Null(provider.PeekCurrent());
        Assert.Equal(0, factoryCalls);

        Task<object> initial = provider.GetCurrent();
        Assert.Same(initial, provider.PeekCurrent());
        Task<object> replacement = provider.ReplaceAfterFailure(initial);
        Assert.Same(replacement, provider.PeekCurrent());
        Assert.Equal(2, factoryCalls);
    }
}
