using System.Text.Json.Serialization;

namespace VoltManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerActionType
{
    Shutdown,
    Restart,
    Sleep
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerMode
{
    Relative,
    Daily
}

public class AutoShutdownSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

    [JsonPropertyName("mode")]
    public ScheduledPowerMode Mode { get; set; } = ScheduledPowerMode.Daily;

    [JsonPropertyName("action")]
    public ScheduledPowerActionType Action { get; set; } = ScheduledPowerActionType.Shutdown;

    // Legacy string action for backwards compat during migration — normalized on load.
    [JsonPropertyName("actionLegacy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionLegacy { get; set; }

    [JsonPropertyName("time")] public string Time { get; set; } = "23:00";

    [JsonPropertyName("executeAtUtc")] public DateTime? ExecuteAtUtc { get; set; }

    [JsonPropertyName("delayMinutes")] public int? DelayMinutes { get; set; }

    [JsonPropertyName("createdAtUtc")] public DateTime? CreatedAtUtc { get; set; }

    [JsonPropertyName("lastTriggeredLocalDate")] public string? LastTriggeredLocalDate { get; set; }
}

/// <summary>Public state for GUI/tray — decoupled from persistence model.</summary>
public record ScheduledPowerActionState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    [JsonPropertyName("mode")] public ScheduledPowerMode Mode { get; init; }

    [JsonPropertyName("action")] public ScheduledPowerActionType Action { get; init; }

    [JsonPropertyName("executeAtUtc")] public DateTime? ExecuteAtUtc { get; init; }

    [JsonPropertyName("delayMinutes")] public int? DelayMinutes { get; init; }

    [JsonPropertyName("remainingSeconds")] public long RemainingSeconds { get; init; }

    [JsonPropertyName("dailyTime")] public string? DailyTime { get; init; }

    [JsonPropertyName("expired")] public bool Expired { get; init; }
}
