using System.Text.Json.Serialization;

namespace VoltManager.Models;

public enum PlanId
{
    PowerSaver,
    Balanced,
    Performance
}

public record PowerPlan
{
    [JsonPropertyName("planId")] public PlanId? PlanId { get; init; }
    [JsonPropertyName("guid")] public string Guid { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("isActive")] public bool IsActive { get; init; }
}

public record ActivePlanReasonState
{
    [JsonPropertyName("source")] public string Source { get; init; } = "system";
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("plan")] public PlanId? Plan { get; init; }
}

/// <summary>
/// Display/sleep inactivity timeouts for a specific Windows power plan.
/// Values are expressed in seconds; 0 means "never".
/// </summary>
public record PowerPlanTimeoutSet
{
    [JsonPropertyName("planGuid")] public string PlanGuid { get; init; } = "";
    [JsonPropertyName("planName")] public string PlanName { get; init; } = "";
    [JsonPropertyName("displayTimeoutAc")] public int DisplayTimeoutAc { get; init; }
    [JsonPropertyName("displayTimeoutDc")] public int DisplayTimeoutDc { get; init; }
    [JsonPropertyName("displayBrightnessAc")] public int? DisplayBrightnessAc { get; init; }
    [JsonPropertyName("displayBrightnessDc")] public int? DisplayBrightnessDc { get; init; }
    [JsonPropertyName("sleepTimeoutAc")] public int SleepTimeoutAc { get; init; }
    [JsonPropertyName("sleepTimeoutDc")] public int SleepTimeoutDc { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// Advanced power plan parameters readable/writable via powercfg.
/// AC = alimentazione di rete; DC = batteria.
/// </summary>
public record PlanParameterSet
{
    [JsonPropertyName("planGuid")]   public string PlanGuid { get; init; } = "";
    [JsonPropertyName("planName")]   public string PlanName { get; init; } = "";

    // Processor state 0-100 %
    [JsonPropertyName("processorMinAc")]  public int ProcessorMinAc  { get; init; } = 5;
    [JsonPropertyName("processorMaxAc")]  public int ProcessorMaxAc  { get; init; } = 100;
    [JsonPropertyName("processorMinDc")]  public int ProcessorMinDc  { get; init; } = 5;
    [JsonPropertyName("processorMaxDc")]  public int ProcessorMaxDc  { get; init; } = 100;

    // Processor Performance Boost Mode (Windows-defined indexes 0-6).
    [JsonPropertyName("boostModeAc")] public int BoostModeAc { get; init; } = 2;
    [JsonPropertyName("boostModeDc")] public int BoostModeDc { get; init; } = 2;

    // PCI Express Active State Power Management:
    //   0 = Off, 1 = Moderate Power Saving, 2 = Maximum Power Saving
    [JsonPropertyName("pcieLinkStateAc")] public int PcieLinkStateAc { get; init; } = 0;
    [JsonPropertyName("pcieLinkStateDc")] public int PcieLinkStateDc { get; init; } = 2;

    // Processor Energy Performance Preference: 0 = performance, 100 = efficiency.
    [JsonPropertyName("processorEppAc")] public int ProcessorEppAc { get; init; } = 50;
    [JsonPropertyName("processorEppDc")] public int ProcessorEppDc { get; init; } = 50;
    [JsonPropertyName("processorEppSupported")] public bool ProcessorEppSupported { get; init; }

    // Minimum percentage of logical processors that must remain unparked.
    [JsonPropertyName("coreParkingMinAc")] public int CoreParkingMinAc { get; init; } = 10;
    [JsonPropertyName("coreParkingMinDc")] public int CoreParkingMinDc { get; init; } = 10;
    [JsonPropertyName("coreParkingSupported")] public bool CoreParkingSupported { get; init; }

    // Disk idle timeout in seconds; 0 = never. May be ignored by some storage/Modern Standby systems.
    [JsonPropertyName("diskIdleAc")] public int DiskIdleAc { get; init; }
    [JsonPropertyName("diskIdleDc")] public int DiskIdleDc { get; init; }
    [JsonPropertyName("diskIdleSupported")] public bool DiskIdleSupported { get; init; }

    // Wake timers: 0 = disabled, 1 = enabled, 2 = important timers only.
    [JsonPropertyName("wakeTimersAc")] public int WakeTimersAc { get; init; } = 2;
    [JsonPropertyName("wakeTimersDc")] public int WakeTimersDc { get; init; }
    [JsonPropertyName("wakeTimersSupported")] public bool WakeTimersSupported { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}
