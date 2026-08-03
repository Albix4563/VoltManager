using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class ProcessParentMapTests
{
    [Fact]
    public void ApplyParentProcessIds_fills_zero_parents_and_preserves_existing()
    {
        var samples = new[]
        {
            new ProcessSample(10, 0, "steam", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(20, 0, "game", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(30, 99, "other", 0, TimeSpan.Zero, DateTime.UnixEpoch),
        };
        var parents = new Dictionary<int, int>
        {
            [10] = 1,
            [20] = 10,
            [30] = 1, // ignored: ParentPid already set
        };

        var applied = ProcessSnapshotProvider.ApplyParentProcessIds(samples, parents);

        Assert.Equal(1, applied[0].ParentPid);
        Assert.Equal(10, applied[1].ParentPid);
        Assert.Equal(99, applied[2].ParentPid);
    }

    [Fact]
    public void ApplyParentProcessIds_ignores_self_parent_and_missing()
    {
        var samples = new[]
        {
            new ProcessSample(5, 0, "a", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            new ProcessSample(6, 0, "b", 0, TimeSpan.Zero, DateTime.UnixEpoch),
        };
        var parents = new Dictionary<int, int>
        {
            [5] = 5, // self
            // 6 missing
        };

        var applied = ProcessSnapshotProvider.ApplyParentProcessIds(samples, parents);
        Assert.Equal(0, applied[0].ParentPid);
        Assert.Equal(0, applied[1].ParentPid);
    }

    [Fact]
    public void Managed_style_parent_map_enables_launcher_ancestry_on_ProcessGraph()
    {
        // Simulates CaptureManaged after Toolhelp parent fill: steam → bootstrap → game.
        var samples = ProcessSnapshotProvider.ApplyParentProcessIds(
            new[]
            {
                new ProcessSample(10, 0, "steam", 0, TimeSpan.Zero, DateTime.UnixEpoch),
                new ProcessSample(20, 0, "bootstrap", 0, TimeSpan.Zero, DateTime.UnixEpoch),
                new ProcessSample(30, 0, "game", 0, TimeSpan.Zero, DateTime.UnixEpoch),
            },
            new Dictionary<int, int>
            {
                [20] = 10,
                [30] = 20,
            });

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
    public void Custom_folder_game_with_launcher_parent_still_classifies_after_parent_map()
    {
        // End-to-end: parent map → graph ancestry → ClassifyProcess launcherChild.
        var samples = ProcessSnapshotProvider.ApplyParentProcessIds(
            new[]
            {
                new ProcessSample(100, 0, "steam", 0, TimeSpan.Zero, DateTime.UnixEpoch),
                new ProcessSample(200, 0, "CoolGame", 900L * 1024 * 1024, TimeSpan.Zero, DateTime.UnixEpoch),
            },
            new Dictionary<int, int> { [200] = 100 });

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
