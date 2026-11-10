using System.Text.RegularExpressions;

namespace VoltManager.Hardware;

internal static class CpuCoreTemperatureSelector
{
    private static readonly string[] ExcludedNames =
        ["Package", "Max", "Average", "Avg", "Distance", "CCD", "Tctl", "Tdie", "Total", "Socket", "Warning", "Critical", "Trip", "Throttle"];
    private static readonly Regex CoreName = new(
        @"^(?:CPU\s*(?:\d+\s+)?)?(?:(?<kind>P|E)[-_]?)?Core\s*#?\s*(?<index>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoreRange = new(
        @"\d+\s*(?:-|to)\s*(?:(?:P-|E-)?Core\s*#?)?\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ponytail: nomi dei sensori Intel/AMD standard; estendere il riconoscimento solo per nuovi formati documentati.
    internal static List<T> Select<T>(IEnumerable<T> readings,
        Func<T, (string? Identifier, string Hardware, string Category, string Type, string Name, double Value)> describe)
    {
        var candidates = new List<(T Reading, string Cpu, int Kind, int Index, string Identity)>();
        foreach (var reading in readings)
        {
            var sensor = describe(reading);
            if (sensor.Category != "cpu" || sensor.Type != "temp" || !double.IsFinite(sensor.Value) || sensor.Value <= 0)
                continue;
            string name = sensor.Name?.Trim() ?? "";
            if (ExcludedNames.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)) || CoreRange.IsMatch(name))
                continue;
            var match = CoreName.Match(name);
            if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index)) continue;
            int kind = match.Groups["kind"].Value.Equals("E", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            string identity = string.IsNullOrEmpty(sensor.Identifier) ? sensor.Hardware + "/" + name : sensor.Identifier;
            int separator = sensor.Identifier?.LastIndexOf('/') ?? -1;
            int categorySeparator = separator > 0 ? sensor.Identifier!.LastIndexOf('/', separator - 1) : -1;
            string cpu = categorySeparator > 0 ? sensor.Identifier![..categorySeparator] : sensor.Hardware;
            candidates.Add((reading, cpu, kind, index, identity));
        }
        return candidates.DistinctBy(item => item.Identity)
            .OrderBy(item => item.Cpu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind).ThenBy(item => item.Index)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Reading).ToList();
    }
}
