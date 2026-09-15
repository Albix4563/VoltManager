using System.Diagnostics;

namespace VoltManager.Services;

/// <summary>
/// GPU usage via "GPU Engine" performance counters (sum of engtype_3D utilization).
/// Counters may not exist (VM, old driver): GpuAvailable=false, never throws past init.
/// Init is async (PERFLIB enumeration is expensive cold); Read() returns 0 until ready.
/// </summary>
public class GpuCounterProvider : IDisposable
{
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(2);
    private Dictionary<string, PerformanceCounter>? _counters;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private double _lastValue;
    private bool _readFaulted; // throttles per-counter read-failure logging
    private volatile bool _ready;
    private bool _disposed;
    private readonly object _gate = new();
    private volatile Gpu3DSnapshot _perProcess = Gpu3DSnapshot.Empty;

    public bool GpuAvailable { get; private set; }
    public DateTime? SampledAtUtc => _lastSampleUtc == DateTime.MinValue ? null : _lastSampleUtc;

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
            var category = new PerformanceCounterCategory("GPU Engine");
            // engtype_3D + High Priority 3D (WDDM 2.x). Compute-only loads still
            // show under 3D on most drivers; avoid summing every engine type.
            var instances = category.GetInstanceNames()
                .Where(IsGpu3DEngine)
                .ToArray();
            _counters ??= new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
            var current = instances.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string removed in _counters.Keys.Where(instance => !current.Contains(instance)).ToArray())
            {
                _counters[removed].Dispose();
                _counters.Remove(removed);
            }
            foreach (string added in current.Where(instance => !_counters.ContainsKey(instance)))
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", added, readOnly: true);
                counter.NextValue();
                _counters.Add(added, counter);
            }
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

    public double Read() => Read(DefaultSampleInterval, collectPerProcess: true);

    public double Read(TimeSpan sampleInterval, bool collectPerProcess, bool force = false)
    {
        if (!_ready) return 0;
        if (!GpuAvailable) return 0;
        DateTime nowUtc = DateTime.UtcNow;
        if (!force && IsSampleFresh(_lastSampleUtc, nowUtc, sampleInterval)) return _lastValue;
        ValidationMetrics.Increment(ValidationCounter.GpuSamples);
        // GPU engine instances come and go per-process; refresh the set periodically.
        if ((nowUtc - _lastRefresh).TotalSeconds > 10)
            RefreshCounters();
        if (_counters == null) return 0;

        double sum = 0;
        bool anyFailed = false;
        // Same pass feeds the per-process map: the PID is already in the instance name.
        Dictionary<int, double>? byPid = collectPerProcess ? new Dictionary<int, double>() : null;
        foreach (var pair in _counters)
        {
            try
            {
                var c = pair.Value;
                float value = c.NextValue();
                sum += value;
                if (byPid != null) AccumulatePerProcess(byPid, pair.Key, value);
            }
            catch (Exception ex) { anyFailed = true; _readFaulted = Logger.WarnOnce(_readFaulted, "GPU counter read failed", ex); }
        }
        if (!anyFailed) _readFaulted = false;
        _lastSampleUtc = nowUtc;
        if (byPid != null) _perProcess = new Gpu3DSnapshot(byPid, nowUtc);
        _lastValue = Math.Min(100, Math.Round(sum, 1));
        return _lastValue;
    }

    internal static bool IsSampleFresh(DateTime lastSampleUtc, DateTime nowUtc)
        => IsSampleFresh(lastSampleUtc, nowUtc, DefaultSampleInterval);

    internal static bool IsSampleFresh(DateTime lastSampleUtc, DateTime nowUtc, TimeSpan sampleInterval)
        => lastSampleUtc != DateTime.MinValue && nowUtc - lastSampleUtc < sampleInterval;

    private void DisposeCounters()
    {
        if (_counters == null) return;
        foreach (var c in _counters.Values) c.Dispose();
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
