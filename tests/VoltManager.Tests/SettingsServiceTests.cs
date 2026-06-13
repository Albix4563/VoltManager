using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VoltManagerTests_" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void FreshLoad_GivesDefaults()
    {
        var svc = new SettingsService(SettingsPath);
        Assert.True(svc.Current.MasterAutomationEnabled);
        Assert.Equal(3, svc.Current.Rules.Count);
        Assert.Equal("Albix4563/power_efficency", svc.Current.UpdateRepo);
        Assert.NotNull(svc.Current.AutoShutdown);
        Assert.False(svc.Current.AutoShutdown.Enabled);
        Assert.Equal("shutdown", svc.Current.AutoShutdown.Action);
        Assert.Equal("23:00", svc.Current.AutoShutdown.Time);
        Assert.NotNull(svc.Current.AutoUpdates);
        Assert.True(svc.Current.AutoUpdates.Enabled);
        Assert.Equal(30, svc.Current.AutoUpdates.IntervalMinutes);
        Assert.Null(svc.Current.AutoUpdates.SnoozedUntilUtc);
        Assert.Null(svc.Current.AutoUpdates.SkippedVersion);
    }

    [Fact]
    public void SaveAndReload_RoundTrips()
    {
        var svc = new SettingsService(SettingsPath);
        svc.Current.MasterAutomationEnabled = false;
        svc.Current.Rules[0].ThresholdPct = 15;
        svc.Current.PlanGuidMap["Performance"] = "11111111-2222-3333-4444-555555555555";
        svc.Current.AutoShutdown.Enabled = true;
        svc.Current.AutoShutdown.Action = "restart";
        svc.Current.AutoShutdown.Time = "22:30";
        svc.Current.AutoShutdown.LastTriggeredLocalDate = "2026-06-13";
        svc.Current.AutoUpdates.Enabled = false;
        svc.Current.AutoUpdates.IntervalMinutes = 45;
        svc.Current.AutoUpdates.SnoozedUntilUtc = new DateTime(2026, 06, 13, 16, 30, 00, DateTimeKind.Utc);
        svc.Current.AutoUpdates.SkippedVersion = "1.2.3";
        svc.Save();

        var reloaded = new SettingsService(SettingsPath);
        Assert.False(reloaded.Current.MasterAutomationEnabled);
        Assert.Equal(15, reloaded.Current.Rules[0].ThresholdPct);
        Assert.Equal("11111111-2222-3333-4444-555555555555", reloaded.Current.PlanGuidMap["Performance"]);
        Assert.True(reloaded.Current.AutoShutdown.Enabled);
        Assert.Equal("restart", reloaded.Current.AutoShutdown.Action);
        Assert.Equal("22:30", reloaded.Current.AutoShutdown.Time);
        Assert.Equal("2026-06-13", reloaded.Current.AutoShutdown.LastTriggeredLocalDate);
        Assert.False(reloaded.Current.AutoUpdates.Enabled);
        Assert.Equal(45, reloaded.Current.AutoUpdates.IntervalMinutes);
        Assert.Equal(new DateTime(2026, 06, 13, 16, 30, 00, DateTimeKind.Utc), reloaded.Current.AutoUpdates.SnoozedUntilUtc);
        Assert.Equal("1.2.3", reloaded.Current.AutoUpdates.SkippedVersion);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{not valid json!!");
        var svc = new SettingsService(SettingsPath);
        Assert.Equal(3, svc.Current.Rules.Count);
    }

    [Fact]
    public void EmptyRules_RestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"rules\":[]}");
        var svc = new SettingsService(SettingsPath);
        Assert.Equal(3, svc.Current.Rules.Count);
    }

    [Fact]
    public void NullAutoShutdown_RestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"autoShutdown\":null}");
        var svc = new SettingsService(SettingsPath);
        Assert.NotNull(svc.Current.AutoShutdown);
        Assert.False(svc.Current.AutoShutdown.Enabled);
        Assert.Equal("shutdown", svc.Current.AutoShutdown.Action);
        Assert.Equal("23:00", svc.Current.AutoShutdown.Time);
    }

    [Fact]
    public void NullAutoUpdates_RestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"autoUpdates\":null}");
        var svc = new SettingsService(SettingsPath);
        Assert.NotNull(svc.Current.AutoUpdates);
        Assert.True(svc.Current.AutoUpdates.Enabled);
        Assert.Equal(30, svc.Current.AutoUpdates.IntervalMinutes);
        Assert.Null(svc.Current.AutoUpdates.SnoozedUntilUtc);
        Assert.Null(svc.Current.AutoUpdates.SkippedVersion);
    }

    [Fact]
    public void InvalidScheduledAction_RestoredToShutdown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"autoShutdown\":{\"enabled\":true,\"action\":\"hibernate\",\"time\":\"21:15\"}}");
        var svc = new SettingsService(SettingsPath);
        Assert.True(svc.Current.AutoShutdown.Enabled);
        Assert.Equal("shutdown", svc.Current.AutoShutdown.Action);
        Assert.Equal("21:15", svc.Current.AutoShutdown.Time);
    }

    [Fact]
    public void InvalidAutoUpdateInterval_RestoredToDefaultAndSkippedVersionNormalized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"autoUpdates\":{\"enabled\":false,\"intervalMinutes\":2,\"skippedVersion\":\"v1.2.3\"}}");
        var svc = new SettingsService(SettingsPath);
        Assert.False(svc.Current.AutoUpdates.Enabled);
        Assert.Equal(30, svc.Current.AutoUpdates.IntervalMinutes);
        Assert.Equal("1.2.3", svc.Current.AutoUpdates.SkippedVersion);
    }

    [Fact]
    public void ExcessiveAutoUpdateInterval_IsCapped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"autoUpdates\":{\"intervalMinutes\":2000}}");
        var svc = new SettingsService(SettingsPath);
        Assert.Equal(1440, svc.Current.AutoUpdates.IntervalMinutes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}