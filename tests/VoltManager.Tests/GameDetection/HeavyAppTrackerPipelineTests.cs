using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public sealed class HeavyAppTrackerPipelineTests
{
    [Fact]
    public void Tracker_keeps_alt_tabbed_game_then_drops_pid_reuse_and_exit()
    {
        DateTime started = new(2026, 9, 21, 7, 0, 0, DateTimeKind.Utc);
        string path = @"D:\SteamLibrary\steamapps\common\Title\Title.exe";
        var tracker = new HeavyAppTracker();
        var game = new DetectedHeavyApp
        {
            ProcessId = 77,
            Name = "Title",
            Path = path,
            Reason = "gameInstallPath",
            Kind = "game",
            WorkingSetMb = 1800,
            StartedAtUtc = started,
        };

        Assert.Single(tracker.Merge(new[] { game }, new[] { new ObservedHeavyProcess(77, path, started, "Title", 1800) }));
        Assert.Single(tracker.Merge(Array.Empty<DetectedHeavyApp>(), new[] { new ObservedHeavyProcess(77, path, started, "Title", 80) }));
        Assert.Empty(tracker.Merge(Array.Empty<DetectedHeavyApp>(), new[] { new ObservedHeavyProcess(77, path, started.AddHours(1), "Title", 1800) }));
        Assert.Empty(tracker.Merge(Array.Empty<DetectedHeavyApp>(), Array.Empty<ObservedHeavyProcess>()));
    }
}
