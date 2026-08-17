namespace VoltManager.Performance;

public sealed record WebViewResourcePlan(
    TimeSpan MetricsInterval,
    bool PublishMetrics,
    bool LowMemoryTarget,
    bool SuspendRenderer,
    bool AllowProcessPolling,
    TimeSpan ProcessPollingInterval);

/// <summary>
/// Single source of truth for WebView elastic-work policy. It deliberately does not
/// own CoreWebView2 so the policy remains deterministic and unit-testable.
/// </summary>
public sealed class WebViewResourceController
{
    public WebViewResourcePlan Resolve(ResourceProfile profile, bool visible)
    {
        if (!visible)
        {
            return new WebViewResourcePlan(
                TimeSpan.Zero,
                PublishMetrics: false,
                LowMemoryTarget: true,
                SuspendRenderer: true,
                AllowProcessPolling: false,
                ProcessPollingInterval: Timeout.InfiniteTimeSpan);
        }

        return profile switch
        {
            ResourceProfile.Critical => new WebViewResourcePlan(
                TimeSpan.FromSeconds(5), true, true, false, false, Timeout.InfiniteTimeSpan),
            ResourceProfile.Gaming => new WebViewResourcePlan(
                TimeSpan.FromSeconds(3), true, true, false, true, TimeSpan.FromSeconds(10)),
            ResourceProfile.Balanced => new WebViewResourcePlan(
                TimeSpan.FromSeconds(2), true, true, false, true, TimeSpan.FromSeconds(6)),
            _ => new WebViewResourcePlan(
                TimeSpan.FromSeconds(1), true, true, false, true, TimeSpan.FromSeconds(3)),
        };
    }
}
