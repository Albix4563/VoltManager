using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class HeavyAppDetectionServiceTests
{
    [Fact]
    public void MergeStickyDetections_DropsStickyWhenPidIsReusedByDifferentPath()
    {
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [4242] = new()
            {
                ProcessId = 4242,
                Name = "Game",
                Path = @"C:\Games\steamapps\common\Game\Game.exe",
                Reason = "gameInstallPath",
                WorkingSetMb = 4096,
            },
        };
        var observed = new[]
        {
            new ObservedHeavyProcess(4242, @"C:\Windows\System32\notepad.exe", DateTimeOffset.FromUnixTimeSeconds(200).UtcDateTime),
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            detected: Array.Empty<DetectedHeavyApp>(),
            observed,
            DateTimeOffset.FromUnixTimeSeconds(300).UtcDateTime);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void MergeStickyDetections_DropsStickyLauncherWhenItBecomesIdle()
    {
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(100).UtcDateTime;
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [4242] = new()
            {
                ProcessId = 4242,
                Name = "ExampleLauncher",
                Path = @"C:\Games\steamapps\common\Example\ExampleLauncher.exe",
                Reason = "windowsGpuPreference",
                WorkingSetMb = 2048,
                StartedAtUtc = startedAt,
            },
        };
        var observed = new[]
        {
            new ObservedHeavyProcess(4242, @"C:\Games\steamapps\common\Example\ExampleLauncher.exe", startedAt,
                Name: "ExampleLauncher", WorkingSetMb: 96),
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            detected: Array.Empty<DetectedHeavyApp>(),
            observed,
            DateTimeOffset.FromUnixTimeSeconds(300).UtcDateTime,
            minWorkingSetMb: 1536);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void MergeStickyDetections_KeepsRealGameWhenMinimizedBelowThreshold()
    {
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(100).UtcDateTime;
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [4242] = new()
            {
                ProcessId = 4242,
                Name = "Example-Win64-Shipping",
                Path = @"C:\Games\steamapps\common\Example\Example-Win64-Shipping.exe",
                Reason = "gameInstallPath",
                WorkingSetMb = 2048,
                StartedAtUtc = startedAt,
            },
        };
        var observed = new[]
        {
            new ObservedHeavyProcess(4242, @"C:\Games\steamapps\common\Example\Example-Win64-Shipping.exe", startedAt,
                Name: "Example-Win64-Shipping", WorkingSetMb: 96),
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            detected: Array.Empty<DetectedHeavyApp>(),
            observed,
            DateTimeOffset.FromUnixTimeSeconds(300).UtcDateTime,
            minWorkingSetMb: 1536);

        var app = Assert.Single(merged);
        Assert.Equal("Example-Win64-Shipping", app.Name);
    }

    [Fact]
    public void Classify_IgnoresIdleLaunchersInsideGameFolders()
    {
        var config = new HeavyAppDetectionSettings { MinWorkingSetMb = 1536 };

        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"C:\Games\steamapps\common\Example\ExampleLauncher.exe",
            "ExampleLauncher",
            workingSetBytes: 96L * 1024 * 1024,
            gpuHighPerformancePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            config);

        Assert.Null(reason);
    }

    [Fact]
    public void Classify_IgnoresIdleLaunchersEvenWhenWindowsGpuPreferenceIsHighPerformance()
    {
        var config = new HeavyAppDetectionSettings { MinWorkingSetMb = 1536 };
        string path = @"C:\Games\steamapps\common\Example\ExampleLauncher.exe";
        var gpuPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HeavyAppDetectionService.NormalizePath(path),
        };

        string? reason = HeavyAppDetectionService.ClassifyProcess(
            path,
            "ExampleLauncher",
            workingSetBytes: 96L * 1024 * 1024,
            gpuPreferences,
            config);

        Assert.Null(reason);
    }

    [Fact]
    public void Classify_KeepsRealGameExecutablesInsideGameFolders()
    {
        var config = new HeavyAppDetectionSettings { MinWorkingSetMb = 1536 };

        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"C:\Games\steamapps\common\Example\Example-Win64-Shipping.exe",
            "Example-Win64-Shipping",
            workingSetBytes: 512L * 1024 * 1024,
            gpuHighPerformancePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            config);

        Assert.Equal("gameInstallPath", reason);
    }
}
