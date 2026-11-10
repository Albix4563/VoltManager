using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Monitoring facade over the shared HardwareAccessCoordinator. When created
/// standalone it owns a coordinator; the application injects one shared instance
/// so monitoring reuses the same LibreHardwareMonitor session.
/// </summary>
public sealed class HardwareSensorProvider : IDisposable
{
    private readonly IHardwareAccess _access;
    private readonly bool _ownsAccess;

    public bool Available => _access.Available;

    public HardwareSensorProvider(IHardwareAccess? access = null)
    {
        _ownsAccess = access == null;
        _access = access ?? new BoundedHardwareAccess(new HardwareAccessCoordinator());
    }

    public SensorReport Read() => _access.Read();

    public SensorReport Read(HardwareSampleRequest request, bool force = false) => _access.Read(request, force);

    public void Invalidate() => _access.Invalidate();

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
    public List<SensorReading> CpuCoreTemps { get; init; } = new();
    public DateTime? SampledAtUtc { get; init; }
    public bool DetailsAvailable { get; init; }
}

/// <summary>Pure sensor-selection logic, kept static.</summary>
public static class SensorAggregation
{
    internal const int MaxUiSensors = 32;

    public static string MapCategory(HardwareType type) => type switch
    {
        HardwareType.Cpu => "cpu",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
        HardwareType.Storage => "storage",
        HardwareType.Memory => "memory",
        _ => "motherboard", // Motherboard, SuperIO, EmbeddedController and other sensor hosts.
    };

    // Failed reads surface as 0 °C (e.g. APUs where the source cannot read a
    // sensor), and warning/critical temperatures are static thresholds, not live
    // data.
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

    internal static List<SensorReading> CapForUi(List<SensorReading> readings)
    {
        if (readings.Count == 0) return readings;
        if (readings.Count <= MaxUiSensors && readings.TrueForAll(r => r.Type is not "clock"))
            return readings;

        var preferred = new List<SensorReading>(Math.Min(MaxUiSensors, readings.Count));
        foreach (var reading in readings)
        {
            if (reading.Type == "temp") preferred.Add(reading);
            if (preferred.Count >= MaxUiSensors) return preferred;
        }
        foreach (var reading in readings)
        {
            if (reading.Type == "clock") preferred.Add(reading);
            if (preferred.Count >= MaxUiSensors) break;
        }
        return preferred;
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

    private static readonly string[] ExcludedCoreTempKeywords =
    [
        "Package",
        "Max",
        "Average",
        "Avg",
        "TjMax",
        "Distance",
        "CCD",
        "Tctl",
        "Tdie",
        "Total",
        "Socket",
        "Warning",
        "Critical",
        "Trip",
        "Throttle",
    ];

    private static readonly Regex CoreTempPattern = new(
        @"^(?:CPU\s*(?:\d+\s+)?)?(?:(?<kind>P|E)[-_]?)?Core\s*#?\s*(?<idx>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoreRangePattern = new(
        @"(?<from>\d+)\s*(?:-|to)\s*(?:(?:P-|E-)?Core\s*#?)?(?<to>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsCoreRange(string name)
    {
        var match = CoreRangePattern.Match(name);
        return match.Success && match.Groups["from"].Value != match.Groups["to"].Value;
    }

    // ponytail: nomi Intel/AMD e identificatori standard; aggiungere topologia esplicita solo per formati non standard.
    public static List<SensorReading> SelectCpuCoreTemps(IReadOnlyList<SensorReading> readings)
    {
        if (readings == null || readings.Count == 0) return new List<SensorReading>();

        var candidates = new List<(SensorReading Reading, (string Group, int CpuIndex) Cpu, int KindPriority, int CoreIndex)>();

        foreach (var r in readings)
        {
            if (r.Category != "cpu" || r.Type != "temp") continue;
            if (double.IsNaN(r.Value) || double.IsInfinity(r.Value) || r.Value <= 0) continue;

            string name = r.Name?.Trim() ?? "";
            if (name.Length == 0) continue;

            if (ContainsAny(name, ExcludedCoreTempKeywords)) continue;
            if (IsCoreRange(name)) continue;

            var match = CoreTempPattern.Match(name);
            if (!match.Success) continue;

            if (!int.TryParse(match.Groups["idx"].Value, out int coreIndex)) continue;
            int kindPriority = string.Equals(match.Groups["kind"].Value, "E", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var cpuKey = ExtractCpuKey(r);

            candidates.Add((r, cpuKey, kindPriority, coreIndex));
        }

        if (candidates.Count == 0) return new List<SensorReading>();

        return candidates
            .DistinctBy(x => !string.IsNullOrEmpty(x.Reading.Identifier)
                ? x.Reading.Identifier
                : $"{x.Reading.Hardware}/{x.Reading.Name}")
            .OrderBy(x => x.Cpu.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Cpu.CpuIndex)
            .ThenBy(x => x.KindPriority)
            .ThenBy(x => x.CoreIndex)
            .ThenBy(x => x.Reading.Identifier ?? x.Reading.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Reading)
            .ToList();
    }

    private static (string Group, int CpuIndex) ExtractCpuKey(SensorReading reading)
    {
        if (!string.IsNullOrWhiteSpace(reading.Identifier))
        {
            var parts = reading.Identifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                int index = int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : 0;
                return (parts[0], index);
            }
        }
        string hw = reading.Hardware ?? "cpu";
        var hwMatch = Regex.Match(hw, @"#?(\d+)$");
        if (hwMatch.Success && int.TryParse(hwMatch.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int hwIndex))
        {
            return (hw[..hwMatch.Index].TrimEnd(), hwIndex);
        }
        return (hw, 0);
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
