using System.Diagnostics;
using System.Management;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>1s metrics loop on a background timer. Degrades per-metric on counter failure.</summary>
public class MonitorService : IDisposable
{
    private readonly PerformanceCounter? _cpuCounter;
    private readonly PerformanceCounter? _diskCounter;
    private readonly GpuCounterProvider _gpu;
    private readonly HardwareSensorProvider _sensors;
    private readonly double _ramTotalGb;
    private System.Threading.Timer? _timer;
    private bool _tickFaulted; // throttles error logging to once per failure streak

    private static readonly int ProcessorCount = Environment.ProcessorCount;
    private Dictionary<int, TimeSpan> _prevProcessCpuTimes = new();
    private DateTime _prevProcessSampleTime;
    private List<ProcessInfo> _cachedTopProcesses = new();
    private int _processTickCounter;

    public event Action<MetricsSnapshot>? MetricsUpdated;
    public MetricsSnapshot Latest { get; private set; } = new();

    public MonitorService(HardwareInfoService hw)
    {
        _ramTotalGb = hw.GetSystemInfo().RamTotalGb;
        _gpu = new GpuCounterProvider();
        _sensors = new HardwareSensorProvider();
        _cpuCounter = TryCreate("Processor", "% Processor Time", "_Total");
        _diskCounter = TryCreate("PhysicalDisk", "% Disk Time", "_Total");
        _cpuCounter?.NextValue(); // prime: first NextValue() always returns 0
        _diskCounter?.NextValue();
    }

    private static PerformanceCounter? TryCreate(string cat, string counter, string instance)
    {
        try { return new PerformanceCounter(cat, counter, instance, readOnly: true); }
        catch { return null; }
    }

    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        try
        {
            double cpu = SafeRead(_cpuCounter);
            double disk = Math.Min(100, SafeRead(_diskCounter));
            double gpu = _gpu.Read();
            var (usedGb, pct) = ReadRam();
            var sensors = _sensors.Read();

            double? finalCpuClock = sensors.CpuClock ?? ReadCpuClockWmi();

            Latest = new MetricsSnapshot
            {
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
                RamClock = sensors.RamClock,
                SensorsAvailable = _sensors.Available,
                Sensors = sensors.Readings,
            };
            MetricsUpdated?.Invoke(Latest);

            if (++_processTickCounter >= 3)
            {
                _processTickCounter = 0;
                UpdateProcesses();
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
    }

    private static double SafeRead(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; }
        catch { return 0; }
    }

    private (double usedGb, double pct) ReadRam()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (var mo in searcher.Get())
            {
                double freeKb = Convert.ToDouble(mo["FreePhysicalMemory"]);
                double totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                double usedKb = totalKb - freeKb;
                return (usedKb / (1024.0 * 1024), totalKb > 0 ? usedKb / totalKb * 100 : 0);
            }
        }
        catch { }
        return (0, 0);
    }

    private double? ReadCpuClockWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
            foreach (var mo in searcher.Get())
            {
                if (mo["CurrentClockSpeed"] != null)
                {
                    return Convert.ToDouble(mo["CurrentClockSpeed"]);
                }
            }
        }
        catch { }
        return null;
    }

    public List<ProcessInfo> GetTopProcesses(int count = 8)
        => _cachedTopProcesses.Take(count).ToList();

    private void UpdateProcesses()
    {
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _prevProcessSampleTime).TotalSeconds;
            bool hasPrevious = _prevProcessCpuTimes.Count > 0 && elapsed > 0 && elapsed < 30;

            var currentTimes = new Dictionary<int, TimeSpan>();
            var results = new List<(string Name, int Pid, double CpuPct, double RamMb)>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;
                    var cpuTime = p.TotalProcessorTime;
                    var mem = p.WorkingSet64;
                    currentTimes[p.Id] = cpuTime;

                    double cpuPct = 0;
                    if (hasPrevious && _prevProcessCpuTimes.TryGetValue(p.Id, out var prev))
                    {
                        cpuPct = (cpuTime - prev).TotalSeconds / elapsed / ProcessorCount * 100;
                        cpuPct = Math.Clamp(cpuPct, 0, 100);
                    }

                    results.Add((p.ProcessName, p.Id, cpuPct, mem / (1024.0 * 1024)));
                }
                catch { }
                finally { p.Dispose(); }
            }

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
        _timer?.Dispose();
        _cpuCounter?.Dispose();
        _diskCounter?.Dispose();
        _gpu.Dispose();
        _sensors.Dispose();
    }
}
