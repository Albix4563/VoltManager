using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class HeavyAppUserRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("foregroundActive", true)]
    [InlineData("launcherChild", true)]
    [InlineData("gameInstallPath", false)]
    public void Explicit_priority_survives_rejected_or_disabled_automatic_classification(string? reason, bool enabled)
    {
        var config = new HeavyAppDetectionSettings
        {
            Enabled = enabled,
            UseResourceHeuristics = false,
            PriorityApplicationPaths = new() { @"C:\Apps\Render.exe" },
        };
        var assessment = new GameDetectionAssessment { PrimaryReason = reason, Score = 10 };
        Assert.Equal("priorityApp", HeavyAppDetectionService.ClassifyKind(
            assessment, @"c:\apps\render.exe", "Render", 512 * 1024 * 1024, config));
        Assert.Null(HeavyAppDetectionService.ClassifyKind(
            assessment, @"c:\apps\other.exe", "Other", 512 * 1024 * 1024, config));
    }

    private static readonly HashSet<string> NoGpuPreferences = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Always_game_path_bypasses_every_heuristic()
    {
        var config = new HeavyAppDetectionSettings
        {
            AlwaysGamePaths = new List<string> { @"D:\Odd\Place\tiny.exe" },
        };

        var result = HeavyAppDetectionService.AssessProcess(
            @"D:\Odd\Place\tiny.exe", "tiny", 40L * 1024 * 1024, NoGpuPreferences, config);

        Assert.Equal("userRule", result.PrimaryReason);
        Assert.Equal(100, result.Score);
        Assert.Equal("confirmed", result.Level);
        Assert.Equal("game", HeavyAppDetectionService.ClassifyKind(
            result, HeavyAppDetectionService.NormalizePath(@"D:\Odd\Place\tiny.exe"),
            "tiny", 40L * 1024 * 1024, config));
    }

    [Fact]
    public void Always_game_path_accepts_a_whole_folder()
    {
        var config = new HeavyAppDetectionSettings
        {
            AlwaysGamePaths = new List<string> { @"D:\MyLibrary" },
        };

        Assert.Equal("userRule", HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibrary\SomeGame\bin\game.exe", "game", 40L * 1024 * 1024, NoGpuPreferences, config));

        // Sibling folder with a shared prefix must not match.
        Assert.NotEqual("userRule", HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibraryOther\app.exe", "app", 40L * 1024 * 1024, NoGpuPreferences, config));
    }

    [Fact]
    public void Never_game_path_excludes_a_process_the_heuristics_would_accept()
    {
        var config = new HeavyAppDetectionSettings
        {
            NeverGamePaths = new List<string> { @"D:\Steam\steamapps\common\Benchmark" },
        };

        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"D:\Steam\steamapps\common\Benchmark\bench.exe", "bench",
            4L * 1024 * 1024 * 1024, NoGpuPreferences, config));
    }

    [Fact]
    public void Never_game_path_wins_over_always_game_path()
    {
        var config = new HeavyAppDetectionSettings
        {
            AlwaysGamePaths = new List<string> { @"D:\Contested\game.exe" },
            NeverGamePaths = new List<string> { @"D:\Contested\game.exe" },
        };

        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"D:\Contested\game.exe", "game", 40L * 1024 * 1024, NoGpuPreferences, config));
    }

    [Fact]
    public void User_lists_are_normalized_on_load()
    {
        var settings = new HeavyAppDetectionSettings
        {
            AlwaysGamePaths = new List<string> { @"D:\A\game.exe", "  ", @"d:\a\GAME.exe", @" D:\B\x.exe " },
            NeverGamePaths = new List<string>(Enumerable.Range(0, 250).Select(i => $@"D:\N\{i}.exe")),
        };

        SettingsService.NormalizeHeavyAppDetectionSettings(settings);

        Assert.Equal(new[] { @"D:\A\game.exe", @"D:\B\x.exe" }, settings.AlwaysGamePaths);
        Assert.Equal(200, settings.NeverGamePaths.Count);
    }

    [Fact]
    public void Null_user_lists_survive_normalization()
    {
        var settings = new HeavyAppDetectionSettings
        {
            AlwaysGamePaths = null!,
            NeverGamePaths = null!,
        };

        SettingsService.NormalizeHeavyAppDetectionSettings(settings);

        Assert.NotNull(settings.AlwaysGamePaths);
        Assert.NotNull(settings.NeverGamePaths);
        Assert.Empty(settings.AlwaysGamePaths);
        Assert.Empty(settings.NeverGamePaths);
    }

    [Fact]
    public void Priority_application_paths_are_exact_executables_and_deduplicated()
    {
        var settings = new HeavyAppDetectionSettings
        {
            PriorityApplicationPaths = new List<string>
            {
                @"C:\Apps\Render.exe",
                @"c:\apps\RENDER.exe",
                @"C:\Apps\NotExecutable.txt",
                "relative.exe",
            },
        };

        SettingsService.NormalizeHeavyAppDetectionSettings(settings);

        Assert.Single(settings.PriorityApplicationPaths);
        Assert.True(HeavyAppDetectionService.MatchesExactExecutablePath(
            @"C:\Apps\Render.exe", settings.PriorityApplicationPaths));
        Assert.False(HeavyAppDetectionService.MatchesExactExecutablePath(
            @"C:\Apps\Child\Render.exe", settings.PriorityApplicationPaths));
    }
}
