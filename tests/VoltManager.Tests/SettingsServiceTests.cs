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
        Assert.NotNull(svc.Current.KeepAwake);
        Assert.False(svc.Current.KeepAwake.Enabled);
        Assert.Null(svc.Current.KeepAwake.LastChangedUtc);
        Assert.NotNull(svc.Current.AppPowerProfiles);
        Assert.True(svc.Current.AppPowerProfiles.Enabled);
        Assert.Empty(svc.Current.AppPowerProfiles.Rules);
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
        svc.Current.KeepAwake.Enabled = true;
        svc.Current.KeepAwake.LastChangedUtc = new DateTime(2026, 06, 13, 17, 00, 00, DateTimeKind.Utc);
        svc.Current.AppPowerProfiles.Enabled = true;
        svc.Current.AppPowerProfiles.Rules.Add(new AppPowerProfileRule
        {
            Id = "nike",
            Enabled = true,
            Name = "Nike",
            Path = @"C:\Apps\nike.exe",
            TargetPlan = PlanId.Performance,
        });
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
        Assert.True(reloaded.Current.KeepAwake.Enabled);
        Assert.Equal(new DateTime(2026, 06, 13, 17, 00, 00, DateTimeKind.Utc), reloaded.Current.KeepAwake.LastChangedUtc);
        Assert.True(reloaded.Current.AppPowerProfiles.Enabled);
        Assert.Single(reloaded.Current.AppPowerProfiles.Rules);
        Assert.Equal(@"C:\Apps\nike.exe", reloaded.Current.AppPowerProfiles.Rules[0].Path);
        Assert.Equal(PlanId.Performance, reloaded.Current.AppPowerProfiles.Rules[0].TargetPlan);
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
    public void NullKeepAwake_RestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"keepAwake\":null}");
        var svc = new SettingsService(SettingsPath);
        Assert.NotNull(svc.Current.KeepAwake);
        Assert.False(svc.Current.KeepAwake.Enabled);
        Assert.Null(svc.Current.KeepAwake.LastChangedUtc);
    }

    [Fact]
    public void NullAppPowerProfiles_RestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"appPowerProfiles\":null}");
        var svc = new SettingsService(SettingsPath);
        Assert.NotNull(svc.Current.AppPowerProfiles);
        Assert.True(svc.Current.AppPowerProfiles.Enabled);
        Assert.Empty(svc.Current.AppPowerProfiles.Rules);
    }

    [Fact]
    public void AppPowerProfiles_NormalizesRules()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          "appPowerProfiles": {
            "enabled": true,
            "rules": [
              { "id": "", "enabled": true, "name": "", "path": "\"C:\\Apps\\Nike.exe\"", "targetPlan": "Performance" },
              { "id": "duplicate", "enabled": true, "name": "Duplicate", "path": "C:\\Apps\\Nike.exe", "targetPlan": "Balanced" },
              { "id": "empty", "enabled": true, "name": "Empty", "path": "", "targetPlan": "Balanced" }
            ]
          }
        }
        """);

        var svc = new SettingsService(SettingsPath);

        Assert.Single(svc.Current.AppPowerProfiles.Rules);
        var rule = svc.Current.AppPowerProfiles.Rules[0];
        Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        Assert.Equal("Nike", rule.Name);
        Assert.Equal(@"C:\Apps\Nike.exe", rule.Path);
        Assert.Equal(PlanId.Performance, rule.TargetPlan);
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
