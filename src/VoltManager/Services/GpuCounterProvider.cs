using System.Diagnostics;

namespace VoltManager.Services;

/// <summary>
/// GPU usage via "GPU Engine" performance counters (sum of engtype_3D utilization).
/// Counters may not exist (VM, old driver): GpuAvailable=false, never throws past init.
/// </summary>
public class GpuCounterProvider : IDisposable
{
    private List<PerformanceCounter>? _counters;
    private DateTime _lastRefresh = DateTime.MinValue;

    public bool GpuAvailable { get; private set; }

    public GpuCounterProvider()
    {
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        try
        {
            DisposeCounters();
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _counters = instances
                .Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, readOnly: true))
                .ToList();
            foreach (var c in _counters) c.NextValue(); // prime
            GpuAvailable = _counters.Count > 0;
            _lastRefresh = DateTime.UtcNow;
        }
        catch
        {
            GpuAvailable = false;
            _counters = null;
        }
    }

    public double Read()
    {
        if (!GpuAvailable) return 0;
        // GPU engine instances come and go per-process; refresh the set periodically.
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds > 10)
            RefreshCounters();
        if (_counters == null) return 0;

        double sum = 0;
        foreach (var c in _counters)
        {
            try { sum += c.NextValue(); }
            catch { }
        }
        return Math.Min(100, Math.Round(sum, 1));
    }

    private void DisposeCounters()
    {
        if (_counters == null) return;
        foreach (var c in _counters) c.Dispose();
        _counters = null;
    }

    public void Dispose() => DisposeCounters();
}
