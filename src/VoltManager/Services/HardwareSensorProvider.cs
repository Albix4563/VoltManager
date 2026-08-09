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
    private bool _readFaulted; // throttles per-update read-failure logging

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
        catch (Exception ex)
        {
            // VM, blocked driver, etc.: sensors stay unavailable. One-shot init,
            // so log once — explains why temp/clock badges show N/D.
            Logger.Warn("Hardware sensors unavailable: " + ex.Message);
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
                _readFaulted = false;
            }
            catch (Exception ex)
            {
                // Keep last good report; never break the metrics loop. Log the
                // first failure of a streak so a persistent sensor fault is visible.
                _readFaulted = Logger.WarnOnce(_readFaulted, "Sensor update failed", ex);
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

            // A writable control is only considered related when LibreHardwareMonitor
            // exposes it directly on this exact fan sensor. Never pair fan/control
            // channels by list position or by a guessed header index.
            IControl? control = sensor.SensorType == SensorType.Fan ? sensor.Control : null;
            bool softwareMode = control?.ControlMode == ControlMode.Software;

            readings.Add(new SensorReading
            {
                Identifier = sensor.Identifier.ToString(),
                Hardware = hardware.Name,
                Category = category,
                Name = sensor.Name,
                Type = type,
                Value = Math.Round(value, type == "clock" ? 0 : (type == "temp" ? 1 : 0)),
                ControlAvailable = control != null,
                ControlMode = control?.ControlMode.ToString(),
                ControlPercent = softwareMode ? Math.Round(control!.SoftwareValue, 1) : null,
                ControlMin = control != null ? Math.Round(control.MinSoftwareValue, 1) : null,
                ControlMax = control != null ? Math.Round(control.MaxSoftwareValue, 1) : null,
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

/// <summary>Pure sensor-selection logic, kept static.</summary>
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
    // 0 MHz clocks are also non-live (parked reporting / failed read).
    public static bool IsLiveReading(string type, string name, float value)
    {
        if (type == "temp")
        {
            if (value <= 0) return false;
            return !name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Trip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Throttle", StringComparison.OrdinalIgnoreCase);
        }
        if (type == "clock") return value > 0;
        return true;
    }

    // AMD: Tctl/Tdie · Intel: CPU Package / Package · hybrid: Core Max · else hottest.
    public static double? SelectCpuTemp(IReadOnlyList<SensorReading> readings)
    {
        var cpuTemps = readings.Where(r => r.Category == "cpu" && r.Type == "temp").ToList();
        if (cpuTemps.Count == 0) return null;

        SensorReading? headline =
            Prefer(cpuTemps, "Tctl/Tdie")
            ?? Prefer(cpuTemps, "Tctl")
            ?? Prefer(cpuTemps, "Tdie")
            ?? PreferExact(cpuTemps, "CPU Package")
            ?? Prefer(cpuTemps, "Package")
            ?? Prefer(cpuTemps, "CPU Die")
            ?? Prefer(cpuTemps, "Core Max")
            ?? Prefer(cpuTemps, "CCD");

        return headline?.Value ?? cpuTemps.Max(r => r.Value);
    }

    public static double? SelectGpuTemp(IReadOnlyList<SensorReading> readings)
    {
        var gpuTemps = readings.Where(r => r.Category == "gpu" && r.Type == "temp").ToList();
        if (gpuTemps.Count == 0) return null;

        SensorReading? headline =
            PreferExact(gpuTemps, "GPU Core")
            ?? Prefer(gpuTemps, "GPU Hot Spot")
            ?? PreferExact(gpuTemps, "Temperature")
            ?? Prefer(gpuTemps, "GPU")
            ?? Prefer(gpuTemps, "Core");

        return (headline ?? gpuTemps.OrderByDescending(r => r.Value).First()).Value;
    }

    public static double? SelectCpuClock(IReadOnlyList<SensorReading> readings)
    {
        var cpuClocks = readings
            .Where(r => r.Category == "cpu" && r.Type == "clock" && !IsNonCoreClock(r.Name))
            .ToList();
        if (cpuClocks.Count == 0) return null;

        var coreClocks = cpuClocks.Where(r => IsCoreClockName(r.Name)).ToList();
        if (coreClocks.Count > 0) return coreClocks.Max(r => r.Value);
        return cpuClocks.Max(r => r.Value);
    }

    public static double? SelectRamClock(IReadOnlyList<SensorReading> readings)
    {
        var memClocks = readings
            .Where(r => (r.Category == "memory" || r.Category == "motherboard") && r.Type == "clock")
            .Where(r => ContainsAny(r.Name, "Memory", "DRAM", "DDR", "RAM"))
            .Where(r => !ContainsAny(r.Name, "Controller", "Fabric", "Uncore", "Infinity"))
            .ToList();
        if (memClocks.Count > 0) return memClocks.Max(r => r.Value);
        return null;
    }

    /// <summary>Effective MHz from base frequency and % Processor Performance.</summary>
    public static double? EffectiveCpuMhz(double baseMhz, double processorPerformancePct)
    {
        if (baseMhz <= 0 || processorPerformancePct <= 0) return null;
        // Cap at 10x base — guards against counter glitches, covers extreme turbo.
        double mhz = baseMhz * (processorPerformancePct / 100.0);
        if (mhz < 100 || mhz > baseMhz * 10) return null;
        return Math.Round(mhz, 0);
    }

    internal static bool IsNonCoreClock(string name) =>
        ContainsAny(name, "Bus", "Fabric", "Infinity", "Memory", "DRAM", "DDR",
            "SOC", "SoC", "Uncore", "Voltage", "FCLK", "MCLK", "UCLK");

    internal static bool IsCoreClockName(string name) =>
        ContainsAny(name, "Core", "CPU", "Thread", "#", "P-Core", "E-Core", "Pcore", "Ecore");

    private static SensorReading? Prefer(List<SensorReading> list, string token) =>
        list.FirstOrDefault(r => r.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static SensorReading? PreferExact(List<SensorReading> list, string name) =>
        list.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
