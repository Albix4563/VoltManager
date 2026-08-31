using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class FeatureExpansionTests
{
    [Fact]
    public void LowBatteryThreshold_UsesConfiguredValueAndRestoresPreviousPlan()
    {
        string path = Path.Combine(Path.GetTempPath(), $"voltmanager-feature-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.PowerSourcePlan.LowBatteryThresholdPercent = 30;
            PowerSourceSnapshot source = new(false, 25);
            var service = new PowerSourcePlanService(settings, () => source);

            var low = service.Evaluate(PlanId.Balanced, manualOverrideActive: false);
            Assert.Equal(PlanId.PowerSaver, low.TargetPlan);
            Assert.True(low.State.LowBatteryActive);
            Assert.Equal(30, low.State.LowBatteryThresholdPercent);

            source = new PowerSourceSnapshot(false, 35);
            var restored = service.Evaluate(PlanId.PowerSaver, manualOverrideActive: false);
            Assert.Equal(PlanId.Balanced, restored.TargetPlan);
            Assert.False(restored.State.LowBatteryActive);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void BatteryHistory_WindowAndCsvKeepUsefulData()
    {
        var now = new DateTime(2026, 8, 31, 10, 0, 0, DateTimeKind.Utc);
        long epoch = new DateTimeOffset(now).ToUnixTimeSeconds();
        var samples = Enumerable.Range(0, 12).Select(i => new BatteryHistorySample
        {
            T = epoch - (11 - i) * 3600,
            Pct = 80 - i,
            W = i % 2 == 0 ? -12.5 : 18.25,
            Ac = i % 2 != 0,
            Temp = 40 + i,
        }).ToList();

        var window = BatteryHistoryService.SelectWindow(samples, now, hours: 6, maxPoints: 4);
        Assert.Equal(4, window.Count);
        Assert.True(window[0].T >= epoch - 6 * 3600);
        Assert.Equal(samples[^1], window[^1]);

        string csv = BatteryHistoryService.ToCsv(samples);
        Assert.StartsWith("timestamp_utc,battery_percent,watts,on_ac,temperature_c", csv);
        Assert.Contains("-12.5", csv);
        Assert.Contains("true", csv);
    }

    [Fact]
    public void AppProfileKeepAwake_RespectsBatterySafetyWithoutChangingManualSetting()
    {
        var cfg = new KeepAwakeSettings
        {
            Enabled = false,
            AutoDisableOnBattery = true,
            MaxMinutes = 30,
        };
        var now = DateTime.UtcNow;

        Assert.Null(PowerAwakeService.SafetyBlockReason(cfg, automationRequested: true, onBattery: false, now));
        Assert.Equal("battery", PowerAwakeService.SafetyBlockReason(cfg, automationRequested: true, onBattery: true, now));
        Assert.False(cfg.Enabled);
    }

    [Fact]
    public void GlobalHotkeyParser_RequiresModifierAndSupportsConfiguredDefaults()
    {
        Assert.True(GlobalHotkeyService.TryParseGesture("Ctrl+Alt+1", out _, out uint saverKey));
        Assert.True(saverKey > 0);
        Assert.True(GlobalHotkeyService.TryParseGesture("Ctrl+Alt+K", out _, out uint awakeKey));
        Assert.True(awakeKey > 0);
        Assert.False(GlobalHotkeyService.TryParseGesture("K", out _, out _));
        Assert.False(GlobalHotkeyService.TryParseGesture("Ctrl+Mouse1", out _, out _));
    }

    [Fact]
    public void NewFeatureSettings_RoundTripThroughSettingsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"voltmanager-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.PowerSourcePlan.LowBatteryThresholdPercent = 31;
            settings.Current.GlobalHotkeys.Enabled = true;
            settings.Current.GlobalHotkeys.PowerSaver = "Ctrl+Shift+9";
            settings.Current.AppPowerProfiles.Rules.Add(new AppPowerProfileRule
            {
                Id = "video",
                Name = "Video",
                Path = @"C:\Apps\Video.exe",
                TargetPlan = PlanId.Balanced,
                KeepAwake = true,
            });
            settings.Save();

            var loaded = new SettingsService(path).Current;
            Assert.Equal(31, loaded.PowerSourcePlan.LowBatteryThresholdPercent);
            Assert.True(loaded.GlobalHotkeys.Enabled);
            Assert.Equal("Ctrl+Shift+9", loaded.GlobalHotkeys.PowerSaver);
            var rule = Assert.Single(loaded.AppPowerProfiles.Rules);
            Assert.Equal(PlanId.Balanced, rule.TargetPlan);
            Assert.True(rule.KeepAwake);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
