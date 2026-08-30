using System.Diagnostics;

namespace VoltManager.Services;

/// <summary>
/// GPU usage via "GPU Engine" performance counters (sum of engtype_3D utilization).
/// Counters may not exist (VM, old driver): GpuAvailable=false, never throws past init.
/// Init is async (PERFLIB enumeration is expensive cold); Read() returns 0 until ready.
/// </summary>
public class GpuCounterProvider : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private List<PerformanceCounter>? _counters;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private double _lastValue;
    private bool _readFaulted; // throttles per-counter read-failure logging
    private volatile bool _ready;
    private bool _disposed;
    private readonly object _gate = new();
    private volatile Gpu3DSnapshot _perProcess = Gpu3DSnapshot.Empty;

    public bool GpuAvailable { get; private set; }

    /// <summary>Per-PID 3D utilization collected by the last <see cref="Read"/>, with its timestamp.</summary>
    public Gpu3DSnapshot PerProcess3D => _perProcess;

    public sealed record Gpu3DSnapshot(IReadOnlyDictionary<int, double> ByPid, DateTime TimestampUtc)
    {
        public static readonly Gpu3DSnapshot Empty =
            new(new Dictionary<int, double>(), DateTime.MinValue);
    }

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
        DateTime nowUtc = DateTime.UtcNow;
        if (IsSampleFresh(_lastSampleUtc, nowUtc)) return _lastValue;
        // GPU engine instances come and go per-process; refresh the set periodically.
        if ((nowUtc - _lastRefresh).TotalSeconds > 10)
            RefreshCounters();
        if (_counters == null) return 0;

        double sum = 0;
        bool anyFailed = false;
        // Same pass feeds the per-process map: the PID is already in the instance name.
        var byPid = new Dictionary<int, double>();
        foreach (var c in _counters)
        {
            try
            {
                float value = c.NextValue();
                sum += value;
                AccumulatePerProcess(byPid, c.InstanceName, value);
            }
            catch (Exception ex) { anyFailed = true; _readFaulted = Logger.WarnOnce(_readFaulted, "GPU counter read failed", ex); }
        }
        if (!anyFailed) _readFaulted = false;
        _lastSampleUtc = nowUtc;
        _perProcess = new Gpu3DSnapshot(byPid, nowUtc);
        _lastValue = Math.Min(100, Math.Round(sum, 1));
        return _lastValue;
    }

    internal static bool IsSampleFresh(DateTime lastSampleUtc, DateTime nowUtc)
        => lastSampleUtc != DateTime.MinValue && nowUtc - lastSampleUtc < SampleInterval;

    private void DisposeCounters()
    {
        if (_counters == null) return;
        foreach (var c in _counters) c.Dispose();
        _counters = null;
    }

    /// <summary>
    /// Extracts the owning PID from a "GPU Engine" instance name
    /// ("pid_9184_luid_0x…_engtype_3D"), or 0 when the name is not usable.
    /// </summary>
    public static int TryParsePidFromInstanceName(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return 0;
        if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return 0;

        int end = instanceName.IndexOf('_', 4);
        if (end < 0) end = instanceName.Length;
        var digits = instanceName.AsSpan(4, end - 4);
        if (digits.IsEmpty) return 0;
        foreach (char ch in digits)
            if (!char.IsAsciiDigit(ch)) return 0;

        return int.TryParse(digits, out int pid) && pid > 0 ? pid : 0;
    }

    /// <summary>
    /// Adds one engine sample to the per-PID map. A process can own several 3D engines,
    /// so samples are summed and clamped to 100.
    /// </summary>
    public static void AccumulatePerProcess(IDictionary<int, double> byPid, string instanceName, double value)
    {
        if (double.IsNaN(value) || value <= 0) return;
        int pid = TryParsePidFromInstanceName(instanceName);
        if (pid == 0) return;

        byPid.TryGetValue(pid, out double current);
        byPid[pid] = Math.Min(100, current + value);
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
