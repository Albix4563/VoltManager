using System.Diagnostics;

namespace VoltManager.Services;

/// <summary>
/// GPU usage via "GPU Engine" performance counters (sum of engtype_3D utilization).
/// Counters may not exist (VM, old driver): GpuAvailable=false, never throws past init.
/// Init is async (PERFLIB enumeration is expensive cold); Read() returns 0 until ready.
/// </summary>
public class GpuCounterProvider : IDisposable
{
    private List<PerformanceCounter>? _counters;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _readFaulted; // throttles per-counter read-failure logging
    private volatile bool _ready;
    private bool _disposed;
    private readonly object _gate = new();

    public bool GpuAvailable { get; private set; }

    public GpuCounterProvider()
    {
        Task.Run(InitCounters);
    }

    private void InitCounters()
    {
        try
        {
            RefreshCounters();
            lock (_gate)
            {
                if (_disposed)
                {
                    DisposeCounters();
                    return;
                }
                _ready = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("GPU counters unavailable: " + ex.Message);
            GpuAvailable = false;
            _counters = null;
        }
    }

    private void RefreshCounters()
    {
        try
        {
            DisposeCounters();
            var category = new PerformanceCounterCategory("GPU Engine");
            // engtype_3D + High Priority 3D (WDDM 2.x). Compute-only loads still
            // show under 3D on most drivers; avoid summing every engine type.
            var instances = category.GetInstanceNames()
                .Where(IsGpu3DEngine)
                .ToArray();
            _counters = instances
                .Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, readOnly: true))
                .ToList();
            foreach (var c in _counters) c.NextValue(); // prime
            GpuAvailable = _counters.Count > 0;
            _lastRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // No "GPU Engine" category (VM/old driver): degrade to unavailable.
            // One-shot — once unavailable, Read() returns early and never re-enters.
            Logger.Warn("GPU counters unavailable: " + ex.Message);
            GpuAvailable = false;
            _counters = null;
        }
    }

    public double Read()
    {
        if (!_ready) return 0;
        if (!GpuAvailable) return 0;
        // GPU engine instances come and go per-process; refresh the set periodically.
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds > 10)
            RefreshCounters();
        if (_counters == null) return 0;

        double sum = 0;
        bool anyFailed = false;
        foreach (var c in _counters)
        {
            try { sum += c.NextValue(); }
            catch (Exception ex) { anyFailed = true; _readFaulted = Logger.WarnOnce(_readFaulted, "GPU counter read failed", ex); }
        }
        if (!anyFailed) _readFaulted = false;
        return Math.Min(100, Math.Round(sum, 1));
    }

    private void DisposeCounters()
    {
        if (_counters == null) return;
        foreach (var c in _counters) c.Dispose();
        _counters = null;
    }

    public static bool IsGpu3DEngine(string instanceName)
    {
        // "...engtype_3D" or "...engtype_High Priority 3D"
        if (instanceName.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) return true;
        return instanceName.EndsWith("engtype_High Priority 3D", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _ready = false;
            DisposeCounters();
        }
    }
}
