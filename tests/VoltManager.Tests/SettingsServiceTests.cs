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
    }

    [Fact]
    public void SaveAndReload_RoundTrips()
    {
        var svc = new SettingsService(SettingsPath);
        svc.Current.MasterAutomationEnabled = false;
        svc.Current.Rules[0].ThresholdPct = 15;
        svc.Current.PlanGuidMap["Performance"] = "11111111-2222-3333-4444-555555555555";
        svc.Save();

        var reloaded = new SettingsService(SettingsPath);
        Assert.False(reloaded.Current.MasterAutomationEnabled);
        Assert.Equal(15, reloaded.Current.Rules[0].ThresholdPct);
        Assert.Equal("11111111-2222-3333-4444-555555555555", reloaded.Current.PlanGuidMap["Performance"]);
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
