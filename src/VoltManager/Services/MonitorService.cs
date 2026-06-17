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
        }
        catch
        {
            // Never let a counter glitch kill the timer loop.
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

    public void Dispose()
    {
        _timer?.Dispose();
        _cpuCounter?.Dispose();
        _diskCounter?.Dispose();
        _gpu.Dispose();
        _sensors.Dispose();
    }
}
