using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ResumeRecoveryCoordinatorTests
{
    [Fact]
    public void Single_resume_runs_once()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        int runs = 0;
        using var coordinator = Create(scheduler, work, () => runs++);

        coordinator.SignalResume();
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.FireLatest();
        Assert.Equal(1, work.Count);
        work.RunNext();

        Assert.Equal(1, runs);
        Assert.Equal(0, work.Count);
    }

    [Fact]
    public void Resume_burst_coalesces_to_one_run()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        int runs = 0;
        using var coordinator = Create(scheduler, work, () => runs++);

        for (int i = 0; i < 25; i++) coordinator.SignalResume();
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.FireLatest();
        work.RunNext();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Resume_during_run_schedules_exactly_one_follow_up()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        int runs = 0;
        using var coordinator = Create(scheduler, work, () => runs++);

        coordinator.SignalResume();
        scheduler.FireLatest();
        Assert.Equal(1, work.Count);

        for (int i = 0; i < 12; i++) coordinator.SignalResume();
        scheduler.FireLatest();
        work.RunNext();
        Assert.Equal(1, work.Count);
        work.RunNext();

        Assert.Equal(2, runs);
        Assert.Equal(0, work.Count);
    }

    [Fact]
    public void Failing_step_does_not_block_later_steps()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        var calls = new List<string>();
        using var coordinator = new ResumeRecoveryCoordinator(
        [
            new("first", () => { calls.Add("first"); throw new InvalidOperationException("expected"); }),
            new("second", () => calls.Add("second")),
            new("third", () => calls.Add("third")),
        ], scheduler: scheduler, queueWork: work.Enqueue);

        coordinator.SignalResume();
        scheduler.FireLatest();
        work.RunNext();

        Assert.Equal(["first", "second", "third"], calls);
    }

    [Fact]
    public void Dispose_cancels_pending_debounce_and_blocks_new_runs()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        int runs = 0;
        var coordinator = Create(scheduler, work, () => runs++);

        coordinator.SignalResume();
        coordinator.Dispose();
        scheduler.FireAll();
        coordinator.SignalResume();
        scheduler.FireAll();

        Assert.Equal(0, work.Count);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Dispose_after_debounce_before_worker_runs_suppresses_queued_recovery()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        int runs = 0;
        var coordinator = Create(scheduler, work, () => runs++);

        coordinator.SignalResume();
        scheduler.FireLatest();
        Assert.Equal(1, work.Count);
        coordinator.Dispose();
        work.RunNext();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void Dispose_is_idempotent_and_concurrent_safe()
    {
        var scheduler = new ManualScheduler();
        var work = new ManualWorkQueue();
        var coordinator = Create(scheduler, work, () => { });
        coordinator.SignalResume();

        Parallel.For(0, 64, _ => coordinator.Dispose());
        scheduler.FireAll();

        Assert.Equal(0, work.Count);
    }

    [Fact]
    public void Two_hundred_create_resume_stop_cycles_do_not_leak_runs()
    {
        int completedRuns = 0;
        for (int cycle = 0; cycle < 200; cycle++)
        {
            var scheduler = new ManualScheduler();
            var work = new ManualWorkQueue();
            var coordinator = Create(scheduler, work, () => completedRuns++);
            coordinator.SignalResume();
            scheduler.FireLatest();
            work.RunNext();
            coordinator.Dispose();
            coordinator.SignalResume();
            scheduler.FireAll();
            Assert.Equal(0, work.Count);
        }

        Assert.Equal(200, completedRuns);
    }

    private static ResumeRecoveryCoordinator Create(ManualScheduler scheduler, ManualWorkQueue work, Action step)
        => new([new ResumeRecoveryStep("step", step)], scheduler: scheduler, queueWork: work.Enqueue);

    private sealed class ManualWorkQueue
    {
        private readonly Queue<Action> _queue = new();
        public int Count => _queue.Count;
        public void Enqueue(Action action) => _queue.Enqueue(action);
        public void RunNext() => _queue.Dequeue()();
    }

    private sealed class ManualScheduler : IResumeRecoveryScheduler
    {
        private readonly List<Entry> _entries = new();
        public int PendingCount => _entries.Count(entry => !entry.Cancelled && !entry.Fired);

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(callback);
            _entries.Add(entry);
            return new CallbackDisposable(onDispose: () => entry.Cancelled = true);
        }

        public void FireLatest()
        {
            Entry entry = _entries.Last(item => !item.Cancelled && !item.Fired);
            entry.Fired = true;
            entry.Callback();
        }

        public void FireAll()
        {
            foreach (Entry entry in _entries.Where(item => !item.Cancelled && !item.Fired).ToArray())
            {
                entry.Fired = true;
                entry.Callback();
            }
        }

        private sealed class Entry(Action callback)
        {
            public Action Callback { get; } = callback;
            public bool Cancelled { get; set; }
            public bool Fired { get; set; }
        }
    }
}
