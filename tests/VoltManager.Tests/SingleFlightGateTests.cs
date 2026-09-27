using VoltManager.Reliability;

namespace VoltManager.Tests;

public sealed class SingleFlightGateTests
{
    [Fact]
    public void Enter_exit_is_reusable()
    {
        var gate = new SingleFlightGate("test");
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public async Task Concurrent_try_enter_has_exactly_one_winner()
    {
        var gate = new SingleFlightGate("stress");
        using var barrier = new Barrier(64);
        int winners = 0;
        Task[] workers = Enumerable.Range(0, 64).Select(_ => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 1000; i++)
            {
                if (gate.TryEnter())
                    Interlocked.Increment(ref winners);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers);
        Assert.Equal(1, winners);
        gate.Exit();
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public void Exit_in_finally_recovers_after_exception()
    {
        var gate = new SingleFlightGate("exception");
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (!gate.TryEnter()) return;
            try { throw new InvalidOperationException("expected"); }
            finally { gate.Exit(); }
        });
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public void Skipped_tick_count_accumulates()
    {
        var gate = new SingleFlightGate("skips");
        Assert.True(gate.TryEnter());
        for (int i = 0; i < 17; i++)
            Assert.False(gate.TryEnter());
        Assert.Equal(17, gate.SkippedTickCount);
        gate.Exit();
    }

    [Fact]
    public void Slow_warning_is_logged_once_and_recovery_once()
    {
        DateTime now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var warnings = new List<string>();
        var infos = new List<string>();
        var gate = new SingleFlightGate(
            "slow operation",
            TimeSpan.FromSeconds(30),
            () => now,
            warnings.Add,
            infos.Add);

        Assert.True(gate.TryEnter());
        now = now.AddSeconds(31);
        for (int i = 0; i < 10; i++) Assert.False(gate.TryEnter());
        Assert.Single(warnings);
        now = now.AddSeconds(2);
        gate.Exit();
        Assert.Single(infos);

        Assert.True(gate.TryEnter());
        gate.Exit();
        Assert.Single(warnings);
        Assert.Single(infos);
    }

    [Fact]
    public void Plan_timer_callback_skips_while_in_flight_then_resumes()
        => AssertGuardedCallbackResumes("plan timer");

    [Fact]
    public void Battery_timer_callback_skips_while_in_flight_then_resumes()
        => AssertGuardedCallbackResumes("battery timer");

    private static void AssertGuardedCallbackResumes(string name)
    {
        var gate = new SingleFlightGate(name);
        int calls = 0;
        Assert.True(gate.TryEnter());
        SingleFlightCallback.Run(gate, () => calls++);
        Assert.Equal(0, calls);
        gate.Exit();
        SingleFlightCallback.Run(gate, () => calls++);
        Assert.Equal(1, calls);
    }
}
