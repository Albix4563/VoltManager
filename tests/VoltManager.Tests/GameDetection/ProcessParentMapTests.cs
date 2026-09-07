using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class ProcessParentMapTests
{
    [Fact]
    public void Toolhelp_filled_parents_enable_launcher_ancestry_on_ProcessGraph()
    {
        // Simulates CaptureManaged after Toolhelp parent fill: steam → bootstrap → game.
        var samples = new[]
        {
            new ProcessSample(10, 0, "steam", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(20, 10, "bootstrap", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(30, 20, "game", 0, TimeSpan.Zero, DateTime.UnixEpoch),
        };

        var graph = new ProcessGraph(samples);
        bool found = graph.TryFindAncestor(
            30,
            process => process.Name.Equals("steam", StringComparison.OrdinalIgnoreCase),
            3,
            out var ancestor);

        Assert.True(found);
        Assert.Equal(10, ancestor.Pid);
    }

    [Fact]
    public void TryReadParentProcessIds_includes_current_process_when_available()
    {
        // Live Windows signal: Toolhelp should see the test host process.
        var map = ProcessSnapshotProvider.TryReadParentProcessIds();
        Assert.NotEmpty(map);
        Assert.True(map.ContainsKey(Environment.ProcessId));
        Assert.True(map[Environment.ProcessId] > 0);
        Assert.NotEqual(Environment.ProcessId, map[Environment.ProcessId]);
    }

    [Fact]
    public void Custom_folder_game_with_launcher_parent_still_classifies()
    {
        // End-to-end: Toolhelp-filled parent map → graph ancestry → ClassifyProcess launcherChild.
        var samples = new[]
        {
            new ProcessSample(100, 0, "steam", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(200, 100, "CoolGame", 900L * 1024 * 1024, TimeSpan.Zero, DateTime.UnixEpoch),
        };

        var graph = new ProcessGraph(samples);
        bool hasLauncher = graph.TryFindAncestor(
            200,
            p => p.Name.Equals("steam", StringComparison.OrdinalIgnoreCase),
            3,
            out _);

        Assert.True(hasLauncher);
        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibrary\CoolGame\CoolGame.exe",
            "CoolGame",
            900L * 1024 * 1024,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Models.HeavyAppDetectionSettings(),
            hasLauncherAncestor: hasLauncher);

        Assert.Equal("launcherChild", reason);
    }
}
