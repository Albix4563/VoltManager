using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class PowerSourcePlanSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("pluggedPlan")] public PlanId PluggedPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("unpluggedMode")] public string UnpluggedMode { get; set; } = "previous";
    [JsonPropertyName("lowBatteryThresholdPercent")] public int LowBatteryThresholdPercent { get; set; } = 20;
}

public record PowerSourcePlanState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("powerSourceKnown")] public bool PowerSourceKnown { get; init; }
    [JsonPropertyName("pluggedIn")] public bool PluggedIn { get; init; }
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; init; }
    [JsonPropertyName("lowBatteryThresholdPercent")] public int LowBatteryThresholdPercent { get; init; } = 20;
    [JsonPropertyName("lowBatteryActive")] public bool LowBatteryActive { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("pluggedPlan")] public PlanId PluggedPlan { get; init; } = PlanId.Performance;
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    [JsonPropertyName("targetPlan")] public PlanId? TargetPlan { get; init; }
    [JsonPropertyName("manualOverrideActive")] public bool ManualOverrideActive { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

