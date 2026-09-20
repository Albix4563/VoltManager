using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ApplicationLifecycleCoordinatorTests
{
    [Fact]
    public void Start_stop_restart_does_not_duplicate_runtime_resources()
    {
        int attach = 0, detach = 0, timers = 0, timerDisposals = 0;
        var actions = new ApplicationLifecycleActions(
            Attach: () => attach++,
            Detach: () => detach++,
            StartServices: () => { },
            StopServices: () => { },
            CreatePlanPollTimer: _ => new CallbackDisposable(
                onCreate: () => timers++, onDispose: () => timerDisposals++),
            CreateBatteryHistoryTimer: _ => new CallbackDisposable(
                onCreate: () => timers++, onDispose: () => timerDisposals++));

        using var coordinator = new ApplicationLifecycleCoordinator(actions);
        coordinator.Start();
        coordinator.Start();
        Assert.Equal(1, attach);
        Assert.Equal(2, timers);

        coordinator.Stop();
        coordinator.Stop();
        Assert.Equal(1, detach);
        Assert.Equal(2, timerDisposals);

        coordinator.Start();
        Assert.Equal(2, attach);
        Assert.Equal(4, timers);
    }

    [Fact]
    public void Stop_continues_after_cleanup_failure_and_cancels_epoch()
    {
        var calls = new List<string>();
        CancellationToken timerEpoch = default;
        var actions = new ApplicationLifecycleActions(
            Attach: () => calls.Add("attach"),
            Detach: () => calls.Add("detach"),
            StartServices: () => calls.Add("start-services"),
            StopServices: () =>
            {
                calls.Add("stop-services");
                throw new InvalidOperationException("expected");
            },
            CreatePlanPollTimer: token =>
            {
                timerEpoch = token;
                return new CallbackDisposable(onDispose: () => calls.Add("plan-dispose"));
            },
            CreateBatteryHistoryTimer: _ =>
                new CallbackDisposable(onDispose: () => calls.Add("battery-dispose")));

        using var coordinator = new ApplicationLifecycleCoordinator(actions);
        coordinator.Start();

        Exception? error = Record.Exception(coordinator.Stop);

        Assert.Null(error);
        Assert.True(timerEpoch.IsCancellationRequested);
        Assert.Contains("plan-dispose", calls);
        Assert.Contains("battery-dispose", calls);
        Assert.Contains("detach", calls);
        Assert.Contains("stop-services", calls);
    }

    [Fact]
    public void Dispose_is_terminal()
    {
        var actions = new ApplicationLifecycleActions(
            () => { }, () => { }, () => { }, () => { },
            _ => new CallbackDisposable(), _ => new CallbackDisposable());
        var coordinator = new ApplicationLifecycleCoordinator(actions);
        coordinator.Start();
        coordinator.Dispose();
        coordinator.Dispose();
        Assert.Throws<ObjectDisposedException>(coordinator.Start);
    }
}
