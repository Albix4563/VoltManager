using System.IO;
using VoltManager.Supervisor;

namespace VoltManager.Tests;

public sealed class RestartPolicyTests
{
    private static readonly RestartPolicyOptions Options = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(8),
        0.20,
        3,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(5));

    [Fact]
    public void Backoff_grows_exponentially_and_caps_at_maximum()
    {
        var policy = new RestartPolicy(Options with { MaximumRestarts = 5 });
        var state = new SupervisorState();
        var jitter = new FixedJitter(0.5);
        DateTimeOffset now = At(12, 0);

        RestartDecision first = policy.RegisterFailure(state, now, TimeSpan.Zero, jitter);
        RestartDecision second = policy.RegisterFailure(state, now.AddSeconds(1), TimeSpan.Zero, jitter);
        RestartDecision third = policy.RegisterFailure(state, now.AddSeconds(2), TimeSpan.Zero, jitter);
        RestartDecision fourth = policy.RegisterFailure(state, now.AddSeconds(3), TimeSpan.Zero, jitter);
        RestartDecision fifth = policy.RegisterFailure(state, now.AddSeconds(4), TimeSpan.Zero, jitter);

        Assert.Equal(TimeSpan.FromSeconds(1), first.Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Delay);
        Assert.Equal(TimeSpan.FromSeconds(4), third.Delay);
        Assert.Equal(TimeSpan.FromSeconds(8), fourth.Delay);
        Assert.Equal(TimeSpan.FromSeconds(8), fifth.Delay);
    }

    [Theory]
    [InlineData(0.0, 800)]
    [InlineData(1.0, 1200)]
    public void Jitter_stays_within_configured_bounds(double unit, int expectedMilliseconds)
    {
        var policy = new RestartPolicy(Options);
        RestartDecision decision = policy.RegisterFailure(
            new SupervisorState(),
            At(12, 0),
            TimeSpan.Zero,
            new FixedJitter(unit));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), decision.Delay);
    }

    [Fact]
    public void Too_many_crashes_blocks_until_attempt_window_expires()
    {
        var policy = new RestartPolicy(Options with { MaximumRestarts = 2 });
        var state = new SupervisorState();
        var jitter = new FixedJitter(0.5);
        DateTimeOffset now = At(12, 0);

        Assert.True(policy.RegisterFailure(state, now, TimeSpan.Zero, jitter).ShouldRestart);
        Assert.True(policy.RegisterFailure(state, now.AddMinutes(1), TimeSpan.Zero, jitter).ShouldRestart);
        RestartDecision blocked = policy.RegisterFailure(state, now.AddMinutes(2), TimeSpan.Zero, jitter);

        Assert.False(blocked.ShouldRestart);
        Assert.Equal(now.AddMinutes(10), blocked.BlockedUntilUtc);
        Assert.True(policy.IsBlocked(state, now.AddMinutes(9)));
        Assert.False(policy.IsBlocked(state, now.AddMinutes(10)));
    }

    [Fact]
    public void Failure_counter_resets_after_attempt_window()
    {
        var policy = new RestartPolicy(Options);
        var state = new SupervisorState();
        var jitter = new FixedJitter(0.5);
        DateTimeOffset now = At(12, 0);

        policy.RegisterFailure(state, now, TimeSpan.Zero, jitter);
        policy.RegisterFailure(state, now.AddMinutes(1), TimeSpan.Zero, jitter);
        RestartDecision afterWindow = policy.RegisterFailure(
            state,
            now.AddMinutes(12),
            TimeSpan.Zero,
            jitter);

        Assert.Equal(1, afterWindow.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(1), afterWindow.Delay);
    }

    [Fact]
    public void Supervisor_arguments_parse_child_reset_and_passthrough_arguments()
    {
        string childPath = Path.GetTempFileName();
        try
        {
            bool parsed = SupervisorOptions.TryParse(
                ["--reset-state", "--child", childPath, "--", "--profile", "quiet"],
                out SupervisorOptions? options,
                out string error);

            Assert.True(parsed, error);
            Assert.NotNull(options);
            Assert.Equal(Path.GetFullPath(childPath), options.ChildPath);
            Assert.True(options.ResetState);
            Assert.Equal(["--profile", "quiet"], options.ChildArguments);
        }
        finally
        {
            File.Delete(childPath);
        }
    }

    [Fact]
    public void Supervisor_rejects_child_option_after_passthrough_separator()
    {
        string childPath = Path.GetTempFileName();
        try
        {
            bool parsed = SupervisorOptions.TryParse(
                ["--", "--child", childPath],
                out SupervisorOptions? options,
                out string error);

            Assert.False(parsed);
            Assert.Null(options);
            Assert.Equal("Missing --child <path>.", error);
        }
        finally
        {
            File.Delete(childPath);
        }
    }

    private static DateTimeOffset At(int hour, int minute)
        => new(2026, 9, 29, hour, minute, 0, TimeSpan.Zero);

    private sealed class FixedJitter(double value) : IJitterSource
    {
        public double NextUnit() => value;
    }
}
