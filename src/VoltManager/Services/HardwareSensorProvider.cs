using LibreHardwareMonitor.Hardware;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Temperature/fan sensors via LibreHardwareMonitor. Init is async (ring0 driver
/// load can take seconds); Read() serves a cached report refreshed at most every
/// <see cref="UpdateIntervalSeconds"/> because LHM updates (SMART, SuperIO) are
/// far heavier than perf counters. Degrades to Available=false, never throws.
/// </summary>
public class HardwareSensorProvider : IDisposable
{
    private const int UpdateIntervalSeconds = 3;

    private Computer? _computer;
    private SensorReport _last = SensorReport.Empty;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly object _gate = new();
    private volatile bool _ready;
    private bool _disposed;

    public bool Available { get; private set; }

    public HardwareSensorProvider()
    {
        Task.Run(InitComputer);
    }

    private void InitComputer()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = true,
            };
            computer.Open();
            lock (_gate)
            {
                if (_disposed)
                {
                    TryClose(computer);
                    return;
                }
                _computer = computer;
                Available = true;
                _ready = true;
            }
        }
        catch
        {
            // VM, blocked driver, etc.: sensors stay unavailable.
        }
    }

    public SensorReport Read()
    {
        if (!_ready) return _last;
        lock (_gate)
        {
            if (_computer == null) return _last;
            if ((DateTime.UtcNow - _lastUpdateUtc).TotalSeconds < UpdateIntervalSeconds) return _last;
            _lastUpdateUtc = DateTime.UtcNow;
            try
            {
                var readings = new List<SensorReading>();
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();
                    Collect(hardware, readings);
                    foreach (var sub in hardware.SubHardware)
                    {
                        sub.Update();
                        Collect(sub, readings);
                    }
                }
                _last = new SensorReport
                {
                    CpuTemp = SensorAggregation.SelectCpuTemp(readings),
                    GpuTemp = SensorAggregation.SelectGpuTemp(readings),
                    CpuClock = SensorAggregation.SelectCpuClock(readings),
                    RamClock = SensorAggregation.SelectRamClock(readings),
                    Readings = readings,
                };
            }
            catch
            {
                // Keep last good report; never break the metrics loop.
            }
            return _last;
        }
    }

    private static void Collect(IHardware hardware, List<SensorReading> readings)
    {
        string category = SensorAggregation.MapCategory(hardware.HardwareType);
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;
            string type = sensor.SensorType switch
            {
                SensorType.Temperature => "temp",
                SensorType.Fan => "fan",
                SensorType.Clock => "clock",
                _ => "",
            };
            if (type.Length == 0) continue;
            if (!SensorAggregation.IsLiveReading(type, sensor.Name, value)) continue;
            readings.Add(new SensorReading
            {
                Hardware = hardware.Name,
                Category = category,
                Name = sensor.Name,
                Type = type,
                Value = Math.Round(value, type == "clock" ? 0 : (type == "temp" ? 1 : 0)),
            });
        }
    }

    private static void TryClose(Computer computer)
    {
        try { computer.Close(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _ready = false;
            if (_computer != null)
            {
                TryClose(_computer);
                _computer = null;
            }
        }
    }
}

public record SensorReport
{
    public static readonly SensorReport Empty = new();

    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? CpuClock { get; init; }
    public double? RamClock { get; init; }
    public List<SensorReading> Readings { get; init; } = new();
}

/// <summary>Pure sensor-selection logic, kept static for unit tests.</summary>
public static class SensorAggregation
{
    public static string MapCategory(HardwareType type) => type switch
    {
        HardwareType.Cpu => "cpu",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
        HardwareType.Storage => "storage",
        HardwareType.Memory => "memory",
        _ => "motherboard", // Motherboard, SuperIO, EmbeddedController, coolers...
    };

    // Failed reads surface as 0 °C (e.g. Lucienne APUs where LHM cannot read the
    // SMU), and NVMe "Warning/Critical Temperature" are static thresholds, not
    // live data. Both would mislead the dashboard. 0 RPM stays: stopped fan is real.
    public static bool IsLiveReading(string type, string name, float value)
    {
        if (type != "temp") return true;
        if (value <= 0) return false;
        return !name.Contains("Warning") && !name.Contains("Critical");
    }

    // AMD exposes "Core (Tctl/Tdie)", Intel "CPU Package"; fall back to hottest core.
    public static double? SelectCpuTemp(IReadOnlyList<SensorReading> readings)
    {
        var cpuTemps = readings.Where(r => r.Category == "cpu" && r.Type == "temp").ToList();
        if (cpuTemps.Count == 0) return null;
        var headline = cpuTemps.FirstOrDefault(r => r.Name.Contains("Tctl/Tdie"))
                    ?? cpuTemps.FirstOrDefault(r => r.Name == "CPU Package");
        return headline?.Value ?? cpuTemps.Max(r => r.Value);
    }

    public static double? SelectGpuTemp(IReadOnlyList<SensorReading> readings)
    {
        var gpuTemps = readings.Where(r => r.Category == "gpu" && r.Type == "temp").ToList();
        if (gpuTemps.Count == 0) return null;
        var headline = gpuTemps.FirstOrDefault(r => r.Name == "GPU Core");
        return (headline ?? gpuTemps[0]).Value;
    }

    public static double? SelectCpuClock(IReadOnlyList<SensorReading> readings)
    {
        var cpuClocks = readings.Where(r => r.Category == "cpu" && r.Type == "clock" && !r.Name.Contains("Bus")).ToList();
        if (cpuClocks.Count == 0) return null;
        var coreClocks = cpuClocks.Where(r => r.Name.Contains("Core") || r.Name.Contains("CPU")).ToList();
        if (coreClocks.Count > 0) return coreClocks.Max(r => r.Value);
        return cpuClocks.Max(r => r.Value);
    }

    public static double? SelectRamClock(IReadOnlyList<SensorReading> readings)
    {
        // LHM might have RAM clocks under RAM (memory) but HardwareType memory is mapped to motherboard.
        // Actually, LibreHardwareMonitor HardwareType.Memory exists. Wait, let's map it.
        var memClocks = readings.Where(r => (r.Category == "memory" || r.Category == "motherboard") && r.Type == "clock" && r.Name.Contains("Memory")).ToList();
        if (memClocks.Count > 0) return memClocks.Max(r => r.Value);
        return null;
    }
}
