using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class ProcessGraphTests
{
    [Fact]
    public void GetAncestors_returns_nearest_first_and_honors_depth()
    {
        var graph = new ProcessGraph(new[]
        {
            Sample(10, 0, "steam"),
            Sample(20, 10, "bootstrap"),
            Sample(30, 20, "game"),
        });

        Assert.Equal(new[] { 20, 10 }, graph.GetAncestors(30, 2).Select(process => process.Pid));
        Assert.Equal(new[] { 20 }, graph.GetAncestors(30, 1).Select(process => process.Pid));
        Assert.Empty(graph.GetAncestors(30, 0));
    }

    [Fact]
    public void GetAncestors_stops_on_cycles_without_returning_origin()
    {
        var graph = new ProcessGraph(new[]
        {
            Sample(10, 20, "a"),
            Sample(20, 10, "b"),
        });

        Assert.Equal(new[] { 20 }, graph.GetAncestors(10, 8).Select(process => process.Pid));
    }

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
    public void Parent_started_after_child_is_rejected_as_pid_reuse()
    {
        var childStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var graph = new ProcessGraph(new[]
        {
            new ProcessSample(10, 0, "steam", 0, TimeSpan.Zero, childStart.AddMinutes(1)),
            new ProcessSample(20, 10, "game", 0, TimeSpan.Zero, childStart),
        });

        Assert.Empty(graph.GetAncestors(20, 3));
        Assert.False(graph.TryFindAncestor(20, process => process.Name == "steam", 3, out _));
    }

    private static ProcessSample Sample(int pid, int parentPid, string name)
        => new(pid, parentPid, name, 0, TimeSpan.Zero, DateTime.UnixEpoch);
}
