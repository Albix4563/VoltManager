using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class AutomationRule
{
    // Comparison: "lt" fires when CPU below threshold, "gt" when above.
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("comparison")] public string Comparison { get; set; } = "gt";
    [JsonPropertyName("thresholdPct")] public double ThresholdPct { get; set; }
    [JsonPropertyName("durationMinutes")] public double DurationMinutes { get; set; } = 1;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; }
}

public class ManualOverride
{
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    [JsonPropertyName("expiresAtUtc")] public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActive(DateTime nowUtc) => ExpiresAtUtc == null || ExpiresAtUtc > nowUtc;
}

public class GlobalHotkeySettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("powerSaver")] public string PowerSaver { get; set; } = "Ctrl+Alt+1";
    [JsonPropertyName("balanced")] public string Balanced { get; set; } = "Ctrl+Alt+2";
    [JsonPropertyName("performance")] public string Performance { get; set; } = "Ctrl+Alt+3";
    [JsonPropertyName("auto")] public string Auto { get; set; } = "Ctrl+Alt+0";
    [JsonPropertyName("keepAwakeToggle")] public string KeepAwakeToggle { get; set; } = "Ctrl+Alt+K";
}

public class CpuAutomationSettings
{
    public const int MinSampleIntervalSeconds = 1;
    public const int MaxSampleIntervalSeconds = 60;

    [JsonPropertyName("sampleIntervalSeconds")] public int SampleIntervalSeconds { get; set; } = 1;

    public void Normalize()
    {
        SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, MinSampleIntervalSeconds, MaxSampleIntervalSeconds);
    }
}

public record CpuAutomationState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("sampleIntervalSeconds")] public int SampleIntervalSeconds { get; init; } = 1;
    [JsonPropertyName("rawCpu")] public double RawCpu { get; init; }
    [JsonPropertyName("averageCpu")] public double AverageCpu { get; init; }
    [JsonPropertyName("sampledAtUtc")] public DateTime? SampledAtUtc { get; init; }
    [JsonPropertyName("candidateRuleId")] public string? CandidateRuleId { get; init; }
    [JsonPropertyName("candidateTargetPlan")] public PlanId? CandidateTargetPlan { get; init; }
    [JsonPropertyName("activePlan")] public PlanId? ActivePlan { get; init; }
    [JsonPropertyName("manualOverrideActive")] public bool ManualOverrideActive { get; init; }
}
