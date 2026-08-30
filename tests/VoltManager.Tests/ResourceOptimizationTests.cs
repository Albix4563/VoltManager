using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ResourceOptimizationTests
{
    [Fact]
    public void App_profile_process_name_prefilter_skips_impossible_executables()
    {
        var rule = new AppPowerProfileRule { Path = @"C:\Apps\Code.exe", Enabled = true };
        var rules = new Dictionary<string, AppPowerProfileRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code.exe"] = rule,
        };

        Assert.True(AppPowerProfileService.CouldMatchProcessName("Code", rules));
        Assert.True(AppPowerProfileService.CouldMatchProcessName("Code.exe", rules));
        Assert.True(AppPowerProfileService.CouldMatchProcessName("", rules));
        Assert.False(AppPowerProfileService.CouldMatchProcessName("chrome", rules));
    }

    [Fact]
    public void Game_detection_reuses_gpu_preferences_for_thirty_seconds()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var service = new HeavyAppDetectionService(new SettingsService(settingsPath));
        int reads = 0;
        HashSet<string> Reader()
        {
            reads++;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Games\Game.exe" };
        }

        var t0 = DateTime.UnixEpoch;
        var first = service.GetCachedGpuPreferences(t0, Reader);
        var cached = service.GetCachedGpuPreferences(t0.AddSeconds(29), Reader);
        var refreshed = service.GetCachedGpuPreferences(t0.AddSeconds(30), Reader);

        Assert.Same(first, cached);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, reads);
    }
}
