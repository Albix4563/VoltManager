using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class HeavyAppDetectionSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("useWindowsGpuPreferences")] public bool UseWindowsGpuPreferences { get; set; } = true;
    [JsonPropertyName("useGameInstallHeuristics")] public bool UseGameInstallHeuristics { get; set; } = true;
    [JsonPropertyName("useResourceHeuristics")] public bool UseResourceHeuristics { get; set; } = true;
    [JsonPropertyName("minWorkingSetMb")] public int MinWorkingSetMb { get; set; } = 1536;
    /// <summary>Executables or folders the user always wants treated as a game.</summary>
    [JsonPropertyName("alwaysGamePaths")] public List<string> AlwaysGamePaths { get; set; } = new();
    /// <summary>Executables or folders that must never be detected. Wins over the include list.</summary>
    [JsonPropertyName("neverGamePaths")] public List<string> NeverGamePaths { get; set; } = new();
    /// <summary>Exact executable paths whose presence protects resources without changing the power plan.</summary>
    [JsonPropertyName("priorityApplicationPaths")] public List<string> PriorityApplicationPaths { get; set; } = new();
}

public class AppPowerProfileRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("keepAwake")] public bool KeepAwake { get; set; }
}

public class AppPowerProfileSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("rules")] public List<AppPowerProfileRule> Rules { get; set; } = new();
}

