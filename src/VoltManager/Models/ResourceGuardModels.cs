using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class StandbyAutoCleanerSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("thresholdGb")] public double ThresholdGb { get; set; } = 2.0;
    [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 60;
    [JsonPropertyName("lastPurgedUtc")] public DateTime? LastPurgedUtc { get; set; }
}

/// <summary>
/// When CPU/GPU stay hot, force a cooler power plan to cut boost heat and draw.
/// Hysteresis (cool threshold) avoids flapping around the trip point.
/// </summary>
public class ThermalGuardSettings
{
    public const double MinThresholdC = 60;
    public const double MaxThresholdC = 105;
    public const int MinHoldSeconds = 5;
    public const int MaxHoldSeconds = 300;

    // Off by default: requires readable sensors; user opts in.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("thresholdCelsius")] public double ThresholdCelsius { get; set; } = 90;
    // Must be below threshold; default = threshold - 8 °C.
    [JsonPropertyName("coolThresholdCelsius")] public double CoolThresholdCelsius { get; set; } = 82;
    [JsonPropertyName("holdSeconds")] public int HoldSeconds { get; set; } = 20;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.PowerSaver;
    [JsonPropertyName("watchGpu")] public bool WatchGpu { get; set; } = true;

    public void Normalize()
    {
        ThresholdCelsius = Math.Clamp(ThresholdCelsius, MinThresholdC, MaxThresholdC);
        CoolThresholdCelsius = Math.Clamp(CoolThresholdCelsius, MinThresholdC - 15, ThresholdCelsius - 1);
        if (CoolThresholdCelsius >= ThresholdCelsius)
            CoolThresholdCelsius = Math.Max(MinThresholdC - 15, ThresholdCelsius - 8);
        HoldSeconds = Math.Clamp(HoldSeconds, MinHoldSeconds, MaxHoldSeconds);
        if (!Enum.IsDefined(TargetPlan))
            TargetPlan = PlanId.PowerSaver;
    }
}

public record ThermalGuardState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("sensorsAvailable")] public bool SensorsAvailable { get; init; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public double? GpuTemp { get; init; }
    [JsonPropertyName("peakTemp")] public double? PeakTemp { get; init; }
    [JsonPropertyName("thresholdCelsius")] public double ThresholdCelsius { get; init; } = 90;
    [JsonPropertyName("coolThresholdCelsius")] public double CoolThresholdCelsius { get; init; } = 82;
    [JsonPropertyName("holdSeconds")] public int HoldSeconds { get; init; } = 20;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.PowerSaver;
    [JsonPropertyName("watchGpu")] public bool WatchGpu { get; init; } = true;
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    [JsonPropertyName("hotHoldSeconds")] public double HotHoldSeconds { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// After the user is idle (no keyboard/mouse input) for a configurable time,
/// switch to a frugal power plan. Restore on activity resume.
/// </summary>
public class IdlePowerGuardSettings
{
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 120;

    // Opt-in: idle plan changes can surprise users who leave long renders running
    // without keep-awake (CPU automation still covers load-based cases).
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("idleMinutes")] public int IdleMinutes { get; set; } = 10;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.PowerSaver;
    /// <summary>When true, only engage while running on battery.</summary>
    [JsonPropertyName("onlyOnBattery")] public bool OnlyOnBattery { get; set; } = true;

    public void Normalize()
    {
        IdleMinutes = Math.Clamp(IdleMinutes, MinIdleMinutes, MaxIdleMinutes);
        if (!Enum.IsDefined(TargetPlan))
            TargetPlan = PlanId.PowerSaver;
    }
}

public record IdlePowerGuardState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("idleMinutes")] public int IdleMinutes { get; init; } = 10;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.PowerSaver;
    [JsonPropertyName("onlyOnBattery")] public bool OnlyOnBattery { get; init; } = true;
    [JsonPropertyName("idleSeconds")] public double IdleSeconds { get; init; }
    [JsonPropertyName("inputAvailable")] public bool InputAvailable { get; init; } = true;
    [JsonPropertyName("onBattery")] public bool? OnBattery { get; init; }
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    // idle | active | waiting | disabled | battery_skip | no_input | manual_override
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
