using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class ProcessGraphTests
{
    [Fact]
    public void TryFindAncestor_returns_first_matching_parent_in_range()
    {
        var graph = new ProcessGraph(new[]
        {
            Sample(10, 0, "steam"),
            Sample(20, 10, "bootstrap"),
            Sample(30, 20, "game"),
        });

        bool found = graph.TryFindAncestor(
            30,
            process => process.Name.Equals("steam", StringComparison.OrdinalIgnoreCase),
            3,
            out var ancestor);

        Assert.True(found);
        Assert.Equal(10, ancestor.Pid);
        Assert.False(graph.TryFindAncestor(30, process => process.Name == "missing", 3, out _));
    }

    [Fact]
    public void TryFindAncestor_honors_max_depth()
    {
        var graph = new ProcessGraph(new[]
        {
            Sample(10, 0, "steam"),
            Sample(20, 10, "bootstrap"),
            Sample(30, 20, "game"),
        });

        // Depth 1 only reaches the immediate parent; the grandparent needs depth 2.
        Assert.False(graph.TryFindAncestor(30, process => process.Name == "steam", 1, out _));
        Assert.True(graph.TryFindAncestor(30, process => process.Name == "steam", 2, out var ancestor));
        Assert.Equal(10, ancestor.Pid);
        Assert.False(graph.TryFindAncestor(30, process => process.Name == "steam", 0, out _));
    }

    [Fact]
    public void TryFindAncestor_stops_on_cycles_without_returning_origin()
    {
        var graph = new ProcessGraph(new[]
        {
            Sample(10, 20, "a"),
            Sample(20, 10, "b"),
        });

        // Walking from 10 finds the immediate parent (20) then stops at the cycle,
        // never looping back to report the origin pid itself as an ancestor.
        Assert.True(graph.TryFindAncestor(10, _ => true, 8, out var ancestor));
        Assert.Equal(20, ancestor.Pid);
        Assert.False(graph.TryFindAncestor(10, process => process.Pid == 10, 8, out _));
    }

    [Fact]
    public void TryFindAncestor_rejects_parent_started_after_child_as_pid_reuse()
    {
        var childStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var graph = new ProcessGraph(new[]
        {
            new ProcessSample(10, 0, "steam", 0, TimeSpan.Zero, childStart.AddMinutes(1)),
            new ProcessSample(20, 10, "game", 0, TimeSpan.Zero, childStart),
        });

        Assert.False(graph.TryFindAncestor(20, process => process.Name == "steam", 3, out _));
    }

    private static ProcessSample Sample(int pid, int parentPid, string name)
        => new(pid, parentPid, name, 0, TimeSpan.Zero, DateTime.UnixEpoch);
}
