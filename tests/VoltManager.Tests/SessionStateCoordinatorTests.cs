using Microsoft.Win32;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class SessionStateCoordinatorTests
{
    [Theory]
    [InlineData(SessionSwitchReason.SessionLock, "Lock")]
    [InlineData(SessionSwitchReason.SessionUnlock, "Unlock")]
    [InlineData(SessionSwitchReason.ConsoleConnect, "ConsoleConnect")]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, "ConsoleDisconnect")]
    [InlineData(SessionSwitchReason.RemoteConnect, "RemoteConnect")]
    [InlineData(SessionSwitchReason.RemoteDisconnect, "RemoteDisconnect")]
    [InlineData(SessionSwitchReason.SessionLogon, "Logon")]
    [InlineData(SessionSwitchReason.SessionLogoff, "Logoff")]
    public void Reason_mapping_is_stable(SessionSwitchReason reason, string expected)
        => Assert.Equal(expected, SessionStateCoordinator.Map(reason).ToString());

    [Fact]
    public void Lock_requests_inactive_sampling_and_unlock_restores_then_recovers_once()
    {
        var source = new FakeSource();
        var states = new List<bool>();
        int recoveries = 0;
        using var coordinator = new SessionStateCoordinator(source, states.Add, () => recoveries++);
        coordinator.Start();

        source.Raise(SessionSwitchReason.SessionLock);
        source.Raise(SessionSwitchReason.SessionUnlock);

        Assert.Equal([true, false], states);
        Assert.Equal(1, recoveries);
        Assert.False(coordinator.IsInactive);
    }

    [Theory]
    [InlineData(SessionSwitchReason.RemoteDisconnect, SessionSwitchReason.RemoteConnect)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.ConsoleConnect)]
    [InlineData(SessionSwitchReason.SessionLogoff, SessionSwitchReason.SessionLogon)]
    public void Disconnect_paths_restore_and_signal_recovery(SessionSwitchReason down, SessionSwitchReason up)
    {
        var source = new FakeSource();
        var states = new List<bool>();
        int recoveries = 0;
        using var coordinator = new SessionStateCoordinator(source, states.Add, () => recoveries++);
        coordinator.Start();

        source.Raise(down);
        source.Raise(up);

        Assert.Equal([true, false], states);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void Window_activation_heals_missed_unlock_and_signals_recovery_once()
    {
        var source = new FakeSource();
        var states = new List<bool>();
        int recoveries = 0;
        using var coordinator = new SessionStateCoordinator(source, states.Add, () => recoveries++);
        coordinator.Start();

        source.Raise(SessionSwitchReason.SessionLock);
        source.Raise(SessionSwitchReason.RemoteDisconnect);
        coordinator.MarkInteractive();
        coordinator.MarkInteractive();

        Assert.Equal([true, false], states);
        Assert.Equal(1, recoveries);
        Assert.False(coordinator.IsInactive);
    }

    [Fact]
    public void Window_activation_while_active_is_a_no_op()
    {
        var source = new FakeSource();
        var states = new List<bool>();
        int recoveries = 0;
        using var coordinator = new SessionStateCoordinator(source, states.Add, () => recoveries++);
        coordinator.Start();

        coordinator.MarkInteractive();
        source.Raise(SessionSwitchReason.SessionLock);
        coordinator.MarkInteractive();
        source.Raise(SessionSwitchReason.SessionUnlock);

        Assert.Equal([true, false], states);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void Multiple_inactive_causes_require_all_causes_to_clear()
    {
        var source = new FakeSource();
        var states = new List<bool>();
        int recoveries = 0;
        using var coordinator = new SessionStateCoordinator(source, states.Add, () => recoveries++);
        coordinator.Start();

        source.Raise(SessionSwitchReason.SessionLock);
        source.Raise(SessionSwitchReason.RemoteDisconnect);
        source.Raise(SessionSwitchReason.SessionUnlock);
        Assert.True(coordinator.IsInactive);
        Assert.Equal(0, recoveries);

        source.Raise(SessionSwitchReason.RemoteConnect);
        Assert.False(coordinator.IsInactive);
        Assert.Equal(1, recoveries);
        Assert.Equal([true, false], states);
    }

    [Fact]
    public void Hundred_event_lock_unlock_storm_coalesces_through_resume_recovery()
    {
        var source = new FakeSource();
        var scheduler = new ResumeScheduler();
        var work = new Queue<Action>();
        int recoveryRuns = 0;
        using var recovery = new ResumeRecoveryCoordinator(
            [new ResumeRecoveryStep("recover", () => recoveryRuns++)],
            scheduler: scheduler,
            queueWork: work.Enqueue);
        using var coordinator = new SessionStateCoordinator(source, _ => { }, recovery.SignalResume);
        coordinator.Start();

        for (int i = 0; i < 100; i++)
            source.Raise(i % 2 == 0 ? SessionSwitchReason.SessionLock : SessionSwitchReason.SessionUnlock);

        Assert.Equal(1, scheduler.PendingCount);
        scheduler.FireLatest();
        Assert.Single(work);
        work.Dequeue()();
        Assert.Equal(1, recoveryRuns);
    }

    [Fact]
    public void Callback_exceptions_never_escape_event_source_thread()
    {
        var source = new FakeSource();
        using var coordinator = new SessionStateCoordinator(
            source,
            _ => throw new InvalidOperationException("state"),
            () => throw new InvalidOperationException("recovery"));
        coordinator.Start();

        Assert.Null(Record.Exception(() => source.Raise(SessionSwitchReason.SessionLock)));
        Assert.Null(Record.Exception(() => source.Raise(SessionSwitchReason.SessionUnlock)));
    }

    [Fact]
    public void Stop_unsubscribes_and_restart_subscribes_once()
    {
        var source = new FakeSource();
        int callbacks = 0;
        using var coordinator = new SessionStateCoordinator(source, _ => callbacks++, () => { });
        coordinator.Start();
        coordinator.Start();
        Assert.Equal(1, source.SubscriberCount);

        coordinator.Stop();
        coordinator.Stop();
        Assert.Equal(0, source.SubscriberCount);
        source.Raise(SessionSwitchReason.SessionLock);
        Assert.Equal(0, callbacks);

        coordinator.Start();
        Assert.Equal(1, source.SubscriberCount);
        source.Raise(SessionSwitchReason.SessionLock);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void Concurrent_stop_is_idempotent_and_unsubscribes()
    {
        var source = new FakeSource();
        using var coordinator = new SessionStateCoordinator(source, _ => { }, () => { });
        coordinator.Start();

        Parallel.For(0, 64, _ => coordinator.Stop());

        Assert.Equal(0, source.SubscriberCount);
    }

    private sealed class FakeSource : ISessionSwitchEventSource
    {
        private SessionSwitchEventHandler? _handlers;
        public int SubscriberCount { get; private set; }

        public event SessionSwitchEventHandler? SessionSwitch
        {
            add { _handlers += value; SubscriberCount++; }
            remove { _handlers -= value; SubscriberCount--; }
        }

        public void Raise(SessionSwitchReason reason)
            => _handlers?.Invoke(this, new SessionSwitchEventArgs(reason));
    }

    private sealed class ResumeScheduler : IResumeRecoveryScheduler
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

        private sealed class Entry(Action callback)
        {
            public Action Callback { get; } = callback;
            public bool Cancelled { get; set; }
            public bool Fired { get; set; }
        }
    }
}
