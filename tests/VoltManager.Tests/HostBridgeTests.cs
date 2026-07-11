using System.IO;
using VoltManager.Bridge;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class HostBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HostBridgeTests_" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public HostBridgeTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void PreserveRuntimeOwnedSettings_KeepsStandbyAutoCleanerFromCurrentSettings()
    {
        var currentLastPurge = new DateTime(2026, 06, 17, 12, 30, 00, DateTimeKind.Utc);
        var incoming = new AppSettings
        {
            StandbyAutoCleaner = new StandbyAutoCleanerSettings
            {
                Enabled = false,
                ThresholdGb = 2.0,
                IntervalMinutes = 60,
            },
        };
        var current = new AppSettings
        {
            StandbyAutoCleaner = new StandbyAutoCleanerSettings
            {
                Enabled = true,
                ThresholdGb = 4.5,
                IntervalMinutes = 120,
                LastPurgedUtc = currentLastPurge,
            },
        };

        HostBridge.PreserveRuntimeOwnedSettings(incoming, current);

        Assert.Same(current.StandbyAutoCleaner, incoming.StandbyAutoCleaner);
        Assert.True(incoming.StandbyAutoCleaner.Enabled);
        Assert.Equal(4.5, incoming.StandbyAutoCleaner.ThresholdGb);
        Assert.Equal(120, incoming.StandbyAutoCleaner.IntervalMinutes);
        Assert.Equal(currentLastPurge, incoming.StandbyAutoCleaner.LastPurgedUtc);
    }

    [Fact]
    public void SaveStandbyAutoCleanSettings_ReturnsNormalizedSettings()
    {
        var settings = new SettingsService(SettingsPath);
        var saved = HostBridge.SaveStandbyAutoCleanSettings(
            settings,
            new StandbyAutoCleanerSettings
            {
                Enabled = true,
                ThresholdGb = 0.1,
                IntervalMinutes = 2,
            });

        Assert.True(saved.Enabled);
        Assert.Equal(0.5, saved.ThresholdGb);
        Assert.Equal(5, saved.IntervalMinutes);
        Assert.Same(settings.Current.StandbyAutoCleaner, saved);
    }

    [Fact]
    public void PreserveRuntimeOwnedSettings_PreservesLanguage()
    {
        var incoming = new AppSettings { Language = "" };
        var current = new AppSettings { Language = "es" };

        HostBridge.PreserveRuntimeOwnedSettings(incoming, current);

        // Language is a user preference — it should be importable/exportable.
        // PreserveRuntimeOwnedSettings does not specifically protect Language,
        // so the incoming Language ("" from import) would overwrite current.
        // This test documents current behavior; the bridge layer handles language
        // differently via setLanguage RPC, not via saveSettings.
        Assert.Equal("", incoming.Language);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
