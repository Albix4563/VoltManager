using VoltManager.Models;

namespace VoltManager.Performance;

/// <summary>
/// Latest-value coalescer for WebView telemetry. Incoming samples always replace the
/// pending snapshot; no queue can build up while UI publishing is throttled.
/// </summary>
public sealed class UiMetricsPublisher
{
    private readonly object _gate = new();
    private MetricsSnapshot? _latest;
    private DateTime? _lastPublishedUtc;

    public bool TryTake(
        MetricsSnapshot incoming,
        WebViewResourcePlan plan,
        DateTime nowUtc,
        out MetricsSnapshot? snapshot)
    {
        lock (_gate)
        {
            _latest = incoming;
            snapshot = null;
            if (!plan.PublishMetrics) return false;

            if (_lastPublishedUtc is DateTime last && nowUtc - last < plan.MetricsInterval)
                return false;

            _lastPublishedUtc = nowUtc;
            snapshot = _latest;
            return true;
        }
    }

    /// <summary>Make the next visible sample publish immediately after a profile/visibility transition.</summary>
    public void ResetCadence()
    {
        lock (_gate) _lastPublishedUtc = null;
    }
}
