using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>Configurable metrics loop on a background timer. Degrades per-metric on counter failure.</summary>
public class MonitorService : IDisposable
{
    private readonly PerformanceCounter? _cpuCounter;
    private readonly PerformanceCounter? _diskCounter;
    private PerformanceCounter? _cpuFreqCounter;
    private PerformanceCounter? _cpuPerfCounter;
    private readonly GpuCounterProvider _gpu;
    private readonly HardwareSensorProvider _sensors;
    private readonly double _ramTotalGb;
    private readonly int _processScanEveryTicks;
    private System.Threading.Timer? _timer;
    private int _tickRunning; // reentrancy guard: skip a tick if the prior one is still in WMI
    private bool _tickFaulted; // throttles error logging to once per failure streak
    private bool _ramFaulted;  // same throttle for the native RAM query
    private bool _clockFaulted; // and for the CPU-clock fallback chain
    private bool _ramClockFaulted;
    private double? _cachedCpuClock;
    private double? _cachedRamClock;
    private DateTime _nextCpuClockRefreshUtc;
    private DateTime _nextRamClockRefreshUtc;
    private volatile bool _cpuInfoReady;
    private bool _disposed;
    private readonly object _cpuInfoGate = new();

    private static readonly TimeSpan CpuClockRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RamClockRefreshInterval = TimeSpan.FromMinutes(5);

    // Above this system-RAM %, skip the full Process.GetProcesses() scan — it is the
    // loop's heaviest allocator, and on a saturated machine that churn is exactly what
    // tips the app over. The top-processes panel just goes briefly stale instead.
    private const double ProcessScanRamCutoffPct = 92;

    // Snapshots older than this force a fresh capture. Kept just under the shortest
    // scanner cadence so the heavy-app and app-profile loops ride along on the same
    // capture instead of enumerating the system again.
    private static readonly TimeSpan ProcessSnapshotMaxAge = TimeSpan.FromMilliseconds(2500);

    private static readonly int ProcessorCount = Environment.ProcessorCount;
    // Two dictionaries swapped each scan: refilling a cleared one avoids allocating a
    // few-hundred-entry table on every process tick.
    private Dictionary<int, TimeSpan> _prevProcessCpuTimes = new();
    private Dictionary<int, TimeSpan> _spareProcessCpuTimes = new();
    private DateTime _prevProcessSampleTime;
    private List<ProcessInfo> _cachedTopProcesses = new();
    private int _processTickCounter;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);

    public event Action<MetricsSnapshot>? MetricsUpdated;
    public MetricsSnapshot Latest { get; private set; } = new();

    /// <summary>Exposes the process-scan cadence bucket chosen at construction (for tests).</summary>
    public int ProcessScanEveryTicks => _processScanEveryTicks;

    // The monitor tick is user-configurable and can run slower than the 5s heavy-app scan.
    // Past this age the per-process GPU map is dropped rather than reused stale.
    private static readonly TimeSpan Gpu3DMaxAge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Per-PID GPU 3D utilization from the last metrics tick. Empty when GPU counters are
    /// unavailable or the last read is too old to be meaningful.
    /// </summary>
    public IReadOnlyDictionary<int, double> ReadGpu3DByProcess()
    {
        var snapshot = _gpu.PerProcess3D;
        return DateTime.UtcNow - snapshot.TimestampUtc > Gpu3DMaxAge
            ? GpuCounterProvider.Gpu3DSnapshot.Empty.ByPid
            : snapshot.ByPid;
    }

    public MonitorService(IHardwareAccess? hardwareAccess = null)
    {
        // No WMI/GetSystemInfo on the startup path: cores + RAM from free OS APIs.
        _ramTotalGb = HardwareInfoService.ReadInstalledRamGb();
        int cores = Environment.ProcessorCount;
        _processScanEveryTicks = _ramTotalGb < 8 || cores <= 2
            ? 10
            : _ramTotalGb < 16 || cores <= 4 ? 6 : 3;
        _gpu = new GpuCounterProvider();
        _sensors = new HardwareSensorProvider(hardwareAccess);
        _cpuCounter = TryCreate("Processor", "% Processor Time", "_Total");
        _diskCounter = TryCreate("PhysicalDisk", "% Disk Time", "_Total");
        _cpuCounter?.NextValue(); // prime: first NextValue() always returns 0
        _diskCounter?.NextValue();
        // Processor Information PERFLIB enumeration is expensive cold — background init
        // mirrors HardwareSensorProvider / GpuCounterProvider.
        Task.Run(InitCpuInfoCounters);
    }

    private void InitCpuInfoCounters()
    {
        try
        {
            // Effective clock = base frequency × (% Processor Performance / 100).
            // WMI CurrentClockSpeed is almost always stuck at base on modern CPUs.
            var freq = TryCreateCpuInfoCounter("Processor Frequency");
            var perf = TryCreateCpuInfoCounter("% Processor Performance");
            // Prime: first NextValue() on a rate counter always returns 0, and with a 2s
            // cached clock that zero would stick for a couple of seconds after every launch.
            freq?.NextValue();
            perf?.NextValue();
            lock (_cpuInfoGate)
            {
                if (_disposed)
                {
                    freq?.Dispose();
                    perf?.Dispose();
                    return;
                }
                _cpuFreqCounter = freq;
                _cpuPerfCounter = perf;
                _cpuInfoReady = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("CPU info counters init failed: " + ex.Message);
        }
    }

    private static PerformanceCounter? TryCreate(string cat, string counter, string instance)
    {
        // One-shot at startup: if the perf-counter category is missing/corrupt the
        // metric degrades to 0 — log once so that's diagnosable, not invisible.
        try { return new PerformanceCounter(cat, counter, instance, readOnly: true); }
        catch (Exception ex) { Logger.Warn($"Perf counter '{cat}\\{counter}' unavailable: " + ex.Message); return null; }
    }

    /// <summary>
    /// Processor Information instances vary: "_Total", "0,_Total", multi-socket.
    /// Prefer global _Total, then first *,_Total, then any.
    /// </summary>
    private static PerformanceCounter? TryCreateCpuInfoCounter(string counterName)
    {
        try
        {
            var category = new PerformanceCounterCategory("Processor Information");
            var instances = category.GetInstanceNames();
            if (instances.Length == 0) return null;

            string? pick =
                instances.FirstOrDefault(i => i.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                ?? instances.FirstOrDefault(i => i.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase))
                ?? instances.FirstOrDefault(i => i.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                ?? instances[0];

            var c = new PerformanceCounter("Processor Information", counterName, pick, readOnly: true);
            c.NextValue();
            return c;
        }
        catch (Exception ex)
        {
            Logger.Warn($"CPU info counter '{counterName}' unavailable: " + ex.Message);
            return null;
        }
    }

    public void Start(TimeSpan? interval = null)
    {
        _interval = NormalizeInterval(interval ?? _interval);
        _timer ??= new System.Threading.Timer(_ => Tick(), null, _interval, _interval);
    }

    public void SetInterval(TimeSpan interval)
    {
        _interval = NormalizeInterval(interval);
        _timer?.Change(_interval, _interval);
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval)
    {
        var min = TimeSpan.FromSeconds(CpuAutomationSettings.MinSampleIntervalSeconds);
        var max = TimeSpan.FromSeconds(CpuAutomationSettings.MaxSampleIntervalSeconds);
        if (interval < min) return min;
        if (interval > max) return max;
        return interval;
    }

    private void Tick()
    {
        // Under saturation a WMI/perf-counter call can stall for seconds; the timer
        // would keep firing and pile overlapping ticks onto the thread pool. Skip
        // any tick that lands while the previous one is still running.
        if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return;
        try
        {
            double cpu = SafeRead(_cpuCounter);
            double disk = Math.Min(100, SafeRead(_diskCounter));
            double gpu = _gpu.Read();
            var (usedGb, pct) = ReadRam();
            var sensors = _sensors.Read();

            double? finalCpuClock = sensors.CpuClock ?? ReadCpuClockFallback();
            double? finalRamClock = sensors.RamClock ?? ReadRamClockWmi();

            Latest = new MetricsSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                Cpu = Math.Round(cpu, 1),
                Gpu = gpu,
                GpuAvailable = _gpu.GpuAvailable,
                RamPct = Math.Round(pct, 1),
                RamUsedGb = Math.Round(usedGb, 1),
                RamTotalGb = _ramTotalGb,
                Disk = Math.Round(disk, 1),
                CpuTemp = sensors.CpuTemp,
                GpuTemp = sensors.GpuTemp,
                CpuClock = finalCpuClock,
                RamClock = finalRamClock,
                SensorsAvailable = _sensors.Available,
                // Cap list pushed 1×/s to the WebView — full LHM dumps allocate hard.
                Sensors = CapSensorsForUi(sensors.Readings),
            };
            MetricsUpdated?.Invoke(Latest);

            if (++_processTickCounter >= _processScanEveryTicks)
            {
                _processTickCounter = 0;
                if (pct < ProcessScanRamCutoffPct) UpdateProcesses();
            }

            if (_tickFaulted)
            {
                _tickFaulted = false;
                Logger.Info("Metrics loop recovered.");
            }
        }
        catch (Exception ex)
        {
            // Never let a counter glitch kill the 1s timer loop. Log only the
            // first failure of a streak so a persistent fault can't spam the log.
            if (!_tickFaulted)
            {
                _tickFaulted = true;
                Logger.Error("Metrics loop tick failed", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private static double SafeRead(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; }
        catch { return 0; }
    }

    // Prefer live temps/fans; drop secondary clocks. Hard ceiling keeps the
    // metrics JSON (and the dashboard sensor DOM) bounded on busy machines.
    private const int MaxUiSensors = 32;

    private static List<SensorReading> CapSensorsForUi(List<SensorReading> readings)
    {
        if (readings.Count == 0) return readings;
        if (readings.Count <= MaxUiSensors && readings.TrueForAll(r => r.Type is not "clock"))
            return readings;

        var preferred = new List<SensorReading>(Math.Min(MaxUiSensors, readings.Count));
        foreach (var r in readings)
        {
            if (r.Type is "temp" or "fan") preferred.Add(r);
            if (preferred.Count >= MaxUiSensors) return preferred;
        }
        // Fill remainder with clocks only if we still have room (rare).
        foreach (var r in readings)
        {
            if (r.Type == "clock") preferred.Add(r);
            if (preferred.Count >= MaxUiSensors) break;
        }
        return preferred;
    }

    private (double usedGb, double pct) ReadRam()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            double total = status.TotalPhysical;
            double used = total - status.AvailablePhysical;
            _ramFaulted = false;
            return (used / (1024.0 * 1024 * 1024), total > 0 ? used / total * 100 : 0);
        }
        catch (Exception ex) { _ramFaulted = Logger.WarnOnce(_ramFaulted, "Native RAM query failed", ex); }
        return (Latest.RamUsedGb, Latest.RamPct);
    }

    /// <summary>
    /// 1) Processor Information effective MHz (base × % performance)
    /// 2) WMI CurrentClockSpeed (base-only, last resort)
    /// </summary>
    private double? ReadCpuClockFallback()
    {
        var now = DateTime.UtcNow;
        if (now < _nextCpuClockRefreshUtc) return _cachedCpuClock;
        _nextCpuClockRefreshUtc = now + CpuClockRefreshInterval;

        try
        {
            double? fromPerf = ReadCpuClockFromPerf();
            if (fromPerf is > 0)
            {
                _clockFaulted = false;
                _cachedCpuClock = fromPerf;
                return _cachedCpuClock;
            }

            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    double current = mo["CurrentClockSpeed"] != null ? Convert.ToDouble(mo["CurrentClockSpeed"]) : 0;
                    double max = mo["MaxClockSpeed"] != null ? Convert.ToDouble(mo["MaxClockSpeed"]) : 0;
                    double pick = current > 0 ? current : max;
                    if (pick <= 0) continue;
                    _clockFaulted = false;
                    _cachedCpuClock = pick;
                    return _cachedCpuClock;
                }
            }
        }
        catch (Exception ex) { _clockFaulted = Logger.WarnOnce(_clockFaulted, "CPU-clock fallback failed", ex); }
        return _cachedCpuClock;
    }

    private double? ReadCpuClockFromPerf()
    {
        if (!_cpuInfoReady) return null;
        var freq = _cpuFreqCounter;
        var perf = _cpuPerfCounter;
        if (freq == null || perf == null) return null;
        try
        {
            double baseMhz = freq.NextValue();
            double perfPct = perf.NextValue();
            return SensorAggregation.EffectiveCpuMhz(baseMhz, perfPct);
        }
        catch (Exception ex)
        {
            _clockFaulted = Logger.WarnOnce(_clockFaulted, "CPU-clock perf counter read failed", ex);
            return null;
        }
    }

    private double? ReadRamClockWmi()
    {
        var now = DateTime.UtcNow;
        if (now < _nextRamClockRefreshUtc) return _cachedRamClock;
        _nextRamClockRefreshUtc = now + RamClockRefreshInterval;

        try
        {
            // ConfiguredClockSpeed = running MT/s; Speed = rated. Prefer configured.
            using var searcher = new ManagementObjectSearcher(
                "SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            using var results = searcher.Get();
            double best = 0;
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    double configured = mo["ConfiguredClockSpeed"] != null
                        ? Convert.ToDouble(mo["ConfiguredClockSpeed"]) : 0;
                    double rated = mo["Speed"] != null ? Convert.ToDouble(mo["Speed"]) : 0;
                    double pick = configured > 0 ? configured : rated;
                    if (pick > best) best = pick;
                }
            }
            if (best > 0)
            {
                _ramClockFaulted = false;
                _cachedRamClock = best;
                return _cachedRamClock;
            }
        }
        catch (Exception ex) { _ramClockFaulted = Logger.WarnOnce(_ramClockFaulted, "RAM-clock WMI query failed", ex); }
        return _cachedRamClock;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    public List<ProcessInfo> GetTopProcesses(int count = 8)
        => _cachedTopProcesses.Take(count).ToList();

    private void UpdateProcesses()
    {
        try
        {
            var snapshot = ProcessSnapshotProvider.Get(ProcessSnapshotMaxAge);
            var now = snapshot.TakenUtc;
            // A shared snapshot can be the very one the previous tick already consumed;
            // there is no new CPU delta to compute, so keep the current list.
            if (now <= _prevProcessSampleTime) return;

            var elapsed = (now - _prevProcessSampleTime).TotalSeconds;
            bool hasPrevious = _prevProcessCpuTimes.Count > 0 && elapsed > 0 && elapsed < 30;

            var currentTimes = _spareProcessCpuTimes;
            currentTimes.Clear();
            var results = new List<(string Name, int Pid, double CpuPct, double RamMb)>(snapshot.Processes.Length);

            foreach (var p in snapshot.Processes)
            {
                if (p.Pid == 0) continue;
                currentTimes[p.Pid] = p.CpuTime;

                double cpuPct = 0;
                if (hasPrevious && _prevProcessCpuTimes.TryGetValue(p.Pid, out var prev))
                {
                    cpuPct = (p.CpuTime - prev).TotalSeconds / elapsed / ProcessorCount * 100;
                    cpuPct = Math.Clamp(cpuPct, 0, 100);
                }

                results.Add((p.Name, p.Pid, cpuPct, p.WorkingSetBytes / (1024.0 * 1024)));
            }

            _spareProcessCpuTimes = _prevProcessCpuTimes;
            _prevProcessCpuTimes = currentTimes;
            _prevProcessSampleTime = now;

            _cachedTopProcesses = results
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessInfo
                {
                    Name = g.Key,
                    Pid = g.First().Pid,
                    CpuPercent = Math.Round(g.Sum(r => r.CpuPct), 1),
                    RamMb = Math.Round(g.Sum(r => r.RamMb), 0),
                    Instances = g.Count(),
                })
                .OrderByDescending(p => p.CpuPercent)
                .ThenByDescending(p => p.RamMb)
                .Take(12)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("Process monitor update failed", ex);
        }
    }

    public void Dispose()
    {
        lock (_cpuInfoGate)
        {
            _disposed = true;
            _cpuInfoReady = false;
            _timer?.Dispose();
            _cpuCounter?.Dispose();
            _diskCounter?.Dispose();
            _cpuFreqCounter?.Dispose();
            _cpuPerfCounter?.Dispose();
            _cpuFreqCounter = null;
            _cpuPerfCounter = null;
        }
        _gpu.Dispose();
        _sensors.Dispose();
    }
}
