using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class UpdateSchedulePolicyTests
{
    [Fact]
    public void Automatic_interval_is_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), UpdateSchedulePolicy.AutomaticCheckInterval);
    }

    [Fact]
    public void Enabled_updates_without_active_suspension_allow_automatic_check()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var settings = new AutoUpdateSettings
        {
            Enabled = true,
            SnoozedUntilUtc = null,
        };

        Assert.True(UpdateSchedulePolicy.IsAutomaticCheckAllowed(settings, now));
    }

    [Fact]
    public void Active_suspension_blocks_automatic_check()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var settings = new AutoUpdateSettings
        {
            Enabled = true,
            SnoozedUntilUtc = now.AddDays(5),
        };

        Assert.False(UpdateSchedulePolicy.IsAutomaticCheckAllowed(settings, now));
    }

    [Fact]
    public void Expired_suspension_allows_automatic_check_again()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var settings = new AutoUpdateSettings
        {
            Enabled = true,
            SnoozedUntilUtc = now.AddSeconds(-1),
        };

        Assert.True(UpdateSchedulePolicy.IsAutomaticCheckAllowed(settings, now));
    }

    [Fact]
    public void Disabled_updates_block_automatic_check()
    {
        var settings = new AutoUpdateSettings { Enabled = false };

        Assert.False(UpdateSchedulePolicy.IsAutomaticCheckAllowed(settings, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(12)]
    public void Settings_suspension_supports_requested_day_presets(int days)
    {
        Assert.True(UpdateSchedulePolicy.IsSupportedSuspensionDays(days));
    }

    [Fact]
    public void Snooze_normalization_allows_twelve_days_but_not_more()
    {
        Assert.Equal(12 * 24 * 60, UpdateSchedulePolicy.NormalizeSnoozeMinutes(12 * 24 * 60));
        Assert.Equal(12 * 24 * 60, UpdateSchedulePolicy.NormalizeSnoozeMinutes(30 * 24 * 60));
    }
}
