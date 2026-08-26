using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Central scheduling rules for automatic update checks and user-requested pauses.
/// Manual update checks intentionally do not use this policy.
/// </summary>
public static class UpdateSchedulePolicy
{
    public const int AutomaticCheckIntervalMinutes = 15;
    public const int MaxSnoozeMinutes = 12 * 24 * 60;

    public static TimeSpan AutomaticCheckInterval
        => TimeSpan.FromMinutes(AutomaticCheckIntervalMinutes);

    public static bool IsAutomaticCheckAllowed(AutoUpdateSettings? settings, DateTime utcNow)
        => settings is { Enabled: true } &&
           !(settings.SnoozedUntilUtc is DateTime snoozedUntil && snoozedUntil > utcNow);

    public static int NormalizeSnoozeMinutes(int minutes)
        => Math.Clamp(minutes, 5, MaxSnoozeMinutes);
}
