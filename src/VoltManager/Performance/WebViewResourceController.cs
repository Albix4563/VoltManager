namespace VoltManager.Performance;

public sealed record WebViewResourcePlan(
    TimeSpan MetricsInterval,
    bool PublishMetrics,
    bool AllowProcessPolling,
    TimeSpan ProcessPollingInterval,
    bool ReducedEffects);

/// <summary>
/// Single source of truth for WebView elastic-work policy. It deliberately does not
/// own CoreWebView2 so the policy remains deterministic and unit-testable.
/// </summary>
public sealed class WebViewResourceController
{
    public WebViewResourcePlan Resolve(ResourceProfile profile, bool visible, bool active = true)
    {
        if (!visible)
        {
            return new WebViewResourcePlan(
                TimeSpan.Zero,
                PublishMetrics: false,
                AllowProcessPolling: false,
                ProcessPollingInterval: Timeout.InfiniteTimeSpan,
                ReducedEffects: true);
        }

        if (!active && profile != ResourceProfile.Critical)
            return new WebViewResourcePlan(
                TimeSpan.FromSeconds(3), true, true, TimeSpan.FromSeconds(10), true);

        return profile switch
        {
            ResourceProfile.Critical => new WebViewResourcePlan(
                TimeSpan.FromSeconds(5), true, false, Timeout.InfiniteTimeSpan, true),
            ResourceProfile.Gaming => new WebViewResourcePlan(
                TimeSpan.FromSeconds(3), true, true, TimeSpan.FromSeconds(10), true),
            ResourceProfile.Workload => new WebViewResourcePlan(
                TimeSpan.FromSeconds(3), true, true, TimeSpan.FromSeconds(10), true),
            ResourceProfile.Balanced => new WebViewResourcePlan(
                TimeSpan.FromSeconds(2), true, true, TimeSpan.FromSeconds(6), true),
            _ => new WebViewResourcePlan(
                TimeSpan.FromSeconds(1), true, true, TimeSpan.FromSeconds(3), false),
        };
    }
}
