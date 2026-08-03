using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class HeavyAppGateTests
{
    private static readonly HashSet<string> NoGpuPreferences = new(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(45.0, "gpu3dSustained")]
    [InlineData(20.0, "gpu3dSustained")]
    [InlineData(8.0, "gpu3dActive")]
    [InlineData(3.0, "gpu3dActive")]
    public void AssessProcess_grades_the_gpu_signal(double gpuPercent, string expectedCode)
    {
        var result = Assess(@"D:\Steam\steamapps\common\Title\Title.exe", "Title", gpu3DPercent: gpuPercent);

        Assert.Contains(result.Evidence, e => e.Code == expectedCode);
        Assert.DoesNotContain(result.Evidence, e => e.Code != expectedCode && e.Code.StartsWith("gpu3d"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.9)]
    public void AssessProcess_ignores_idle_gpu_usage(double gpuPercent)
    {
        var result = Assess(@"D:\Steam\steamapps\common\Title\Title.exe", "Title", gpu3DPercent: gpuPercent);

        Assert.DoesNotContain(result.Evidence, e => e.Code.StartsWith("gpu3d"));
    }

    [Fact]
    public void AssessProcess_adds_d3d_fullscreen_only_when_attributed()
    {
        var with = Assess(@"D:\Games\Indie\Indie.exe", "Indie",
            gpu3DPercent: 40, isForeground: true, d3dFullscreen: true);
        var without = Assess(@"D:\Games\Indie\Indie.exe", "Indie",
            gpu3DPercent: 40, isForeground: true, d3dFullscreen: false);

        Assert.Contains(with.Evidence, e => e.Code == "d3dFullscreen");
        Assert.DoesNotContain(without.Evidence, e => e.Code == "d3dFullscreen");
    }

    [Fact]
    public void Sustained_gpu_usage_opens_the_assessment_for_an_unknown_path()
    {
        // Below the foreground candidate floor and outside every known game folder: without
        // the GPU signal this process would never even be assessed.
        Assert.Equal("gpuActive", HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyStuff\Indie\Indie.exe", "Indie", 700L * 1024 * 1024,
            NoGpuPreferences, new HeavyAppDetectionSettings(), gpu3DPercent: 55));
    }

    [Fact]
    public void Sustained_gpu_usage_does_not_open_the_assessment_for_denied_processes()
    {
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome", 700L * 1024 * 1024,
            NoGpuPreferences, new HeavyAppDetectionSettings(), gpu3DPercent: 95));
    }

    [Fact]
    public void Indie_game_in_a_custom_folder_reaches_the_game_gate_on_runtime_alone()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var result = Assess(@"D:\MyStuff\Indie\Indie.exe", "Indie",
            workingSetBytes: 700L * 1024 * 1024,
            startedAtUtc: now.AddMinutes(-5), nowUtc: now,
            isForeground: true, gpu3DPercent: 55, d3dFullscreen: true);

        Assert.True(result.Score >= GameConfidenceScorer.GameThreshold);
        Assert.Equal("game", HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(@"D:\MyStuff\Indie\Indie.exe"),
            "Indie", 700L * 1024 * 1024, new HeavyAppDetectionSettings()));
    }

    [Fact]
    public void Fullscreen_electron_app_is_neither_game_nor_heavy_app()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"C:\Users\Someone\AppData\Local\Notionish\Notionish.exe";
        long workingSet = 800L * 1024 * 1024;
        var config = new HeavyAppDetectionSettings();

        var result = Assess(path, "Notionish",
            workingSetBytes: workingSet,
            startedAtUtc: now.AddMinutes(-30), nowUtc: now,
            isForeground: true, gpu3DPercent: 1.5);

        Assert.True(result.Score < GameConfidenceScorer.GameThreshold);
        Assert.Null(HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(path), "Notionish", workingSet, config));
    }

    [Fact]
    public void Heavy_render_workload_is_a_heavy_app_but_not_a_game()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe";
        long workingSet = 6L * 1024 * 1024 * 1024;
        var config = new HeavyAppDetectionSettings();

        var result = Assess(path, "blender",
            workingSetBytes: workingSet,
            startedAtUtc: now.AddMinutes(-10), nowUtc: now,
            isForeground: true, gpu3DPercent: 95);

        Assert.True(result.Score < GameConfidenceScorer.GameThreshold);
        Assert.Equal("heavyApp", HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(path), "blender", workingSet, config));
    }

    [Fact]
    public void Known_game_install_path_stays_detected_without_any_runtime_signal()
    {
        // No GPU counters (VM / old driver) must not silently disable path-based detection.
        string path = @"D:\Steam\steamapps\common\Stardewish\Stardewish.exe";
        long workingSet = 200L * 1024 * 1024;
        var config = new HeavyAppDetectionSettings();

        var result = Assess(path, "Stardewish", workingSetBytes: workingSet);

        Assert.True(result.Score < GameConfidenceScorer.GameThreshold);
        Assert.Equal("heavyApp", HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(path), "Stardewish", workingSet, config));
    }

    [Fact]
    public void Light_steam_game_becomes_a_game_once_it_touches_the_gpu()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\Steam\steamapps\common\Stardewish\Stardewish.exe";
        long workingSet = 200L * 1024 * 1024;

        var result = Assess(path, "Stardewish",
            workingSetBytes: workingSet,
            startedAtUtc: now.AddMinutes(-5), nowUtc: now,
            isForeground: true, gpu3DPercent: 8);

        Assert.True(result.Score >= GameConfidenceScorer.GameThreshold);
        Assert.Equal("game", HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(path), "Stardewish", workingSet,
            new HeavyAppDetectionSettings()));
    }

    [Fact]
    public void Sticky_only_keeps_processes_that_reached_the_game_gate()
    {
        var sticky = new Dictionary<int, DetectedHeavyApp>();
        var heavy = new DetectedHeavyApp
        {
            ProcessId = 10,
            Name = "blender",
            Path = @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
            Reason = "resourceHeuristic",
            Kind = "heavyApp",
            WorkingSetMb = 6000,
        };
        var game = new DetectedHeavyApp
        {
            ProcessId = 11,
            Name = "Title",
            Path = @"D:\Steam\steamapps\common\Title\Title.exe",
            Reason = "gameInstallPath",
            Kind = "game",
            WorkingSetMb = 3000,
        };

        HeavyAppDetectionService.MergeStickyDetections(
            sticky, new[] { heavy, game }, ObservedFor(heavy, game), DateTime.UtcNow);

        Assert.False(sticky.ContainsKey(10));
        Assert.True(sticky.ContainsKey(11));
    }

    [Fact]
    public void Sticky_keeps_an_alt_tabbed_game_but_releases_a_finished_heavy_app()
    {
        var game = new DetectedHeavyApp
        {
            ProcessId = 11,
            Name = "Title",
            Path = @"D:\Steam\steamapps\common\Title\Title.exe",
            Reason = "gameInstallPath",
            Kind = "game",
            WorkingSetMb = 3000,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
        };
        var sticky = new Dictionary<int, DetectedHeavyApp> { [11] = game };

        // Still alive but no longer classified this scan (memory trimmed after alt-tab).
        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky, Array.Empty<DetectedHeavyApp>(), ObservedFor(game), DateTime.UtcNow);

        Assert.Single(merged);
        Assert.Equal(11, merged[0].ProcessId);
    }

    private static GameDetectionAssessment Assess(
        string path,
        string processName,
        long workingSetBytes = 512L * 1024 * 1024,
        DateTime? startedAtUtc = null,
        DateTime? nowUtc = null,
        bool isForeground = false,
        double gpu3DPercent = 0,
        bool d3dFullscreen = false)
        => HeavyAppDetectionService.AssessProcess(
            path, processName, workingSetBytes, NoGpuPreferences, new HeavyAppDetectionSettings(),
            startedAtUtc, nowUtc, hasLauncherAncestor: false, isForeground: isForeground,
            gpu3DPercent: gpu3DPercent, d3dFullscreen: d3dFullscreen);

    private static ObservedHeavyProcess[] ObservedFor(params DetectedHeavyApp[] apps)
        => apps.Select(a => new ObservedHeavyProcess(
            a.ProcessId, a.Path, a.StartedAtUtc, a.Name, a.WorkingSetMb)).ToArray();
}
