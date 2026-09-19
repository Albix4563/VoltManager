using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class KeepAwakeSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("lastChangedUtc")] public DateTime? LastChangedUtc { get; set; }

    /// <summary>
    /// When true, keep-awake turns itself off as soon as the machine runs on battery
    /// so overnight downloads do not silently drain the pack.
    /// </summary>
    [JsonPropertyName("autoDisableOnBattery")] public bool AutoDisableOnBattery { get; set; } = true;

    /// <summary>
    /// Optional hard cap in minutes (0 = unlimited). Measured from lastChangedUtc
    /// when the feature was last enabled.
    /// </summary>
    [JsonPropertyName("maxMinutes")] public int MaxMinutes { get; set; } = 0;

    public void Normalize()
    {
        if (MaxMinutes < 0) MaxMinutes = 0;
        if (MaxMinutes > 24 * 60) MaxMinutes = 24 * 60; // cap at 24h
    }
}

public record KeepAwakeState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("applied")] public bool Applied { get; init; }
    [JsonPropertyName("automationRequested")] public bool AutomationRequested { get; init; }
    [JsonPropertyName("lastChangedUtc")] public DateTime? LastChangedUtc { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    // Safety options (echoed for the UI)
    [JsonPropertyName("autoDisableOnBattery")] public bool AutoDisableOnBattery { get; init; } = true;
    [JsonPropertyName("maxMinutes")] public int MaxMinutes { get; init; }
    // Seconds left before auto-timeout; null when unlimited or inactive.
    [JsonPropertyName("remainingSeconds")] public long? RemainingSeconds { get; init; }
    // none | battery | timeout
    [JsonPropertyName("lastAutoDisableReason")] public string? LastAutoDisableReason { get; init; }
}
