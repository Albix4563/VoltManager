using LibreHardwareMonitor.Hardware;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Monitoring facade over the shared HardwareAccessCoordinator. When created
/// standalone it owns a coordinator; the application injects one shared instance
/// so monitoring and fan control never open competing LibreHardwareMonitor sessions.
/// </summary>
public sealed class HardwareSensorProvider : IDisposable
{
    private readonly IHardwareAccess _access;
    private readonly bool _ownsAccess;

    public bool Available => _access.Available;

    public HardwareSensorProvider(IHardwareAccess? access = null)
    {
        _ownsAccess = access == null;
        _access = access ?? new HardwareAccessCoordinator();
    }

    public SensorReport Read() => _access.Read();

    public void Dispose()
    {
        if (_ownsAccess) _access.Dispose();
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

    // Failed reads surface as 0 °C (e.g. APUs where the source cannot read a
    // sensor), and warning/critical temperatures are static thresholds, not live
    // data. 0 RPM stays because a stopped fan can be a valid live reading.
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
