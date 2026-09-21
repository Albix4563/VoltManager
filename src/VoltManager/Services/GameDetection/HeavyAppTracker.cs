namespace VoltManager.Services.GameDetection;

internal sealed class HeavyAppTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, DetectedHeavyApp> _sticky = new();

    internal List<DetectedHeavyApp> Merge(
        IEnumerable<DetectedHeavyApp> detected,
        IEnumerable<ObservedHeavyProcess> observed,
        int minWorkingSetMb = 1536)
    {
        lock (_gate)
        {
            return HeavyAppDetectionService.MergeStickyDetections(
                _sticky, detected, observed, minWorkingSetMb);
        }
    }

    internal void Reset()
    {
        lock (_gate)
            _sticky.Clear();
    }
}
