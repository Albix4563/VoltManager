using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class ScheduledPowerActionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VoltManagerTests_" + Guid.NewGuid().ToString("N"));
    private readonly FakePowerActionExecutor _executor = new();
    private readonly FakeClock _clock = new();
    private SettingsService _settings = null!;

    private SettingsService CreateSettings()
    {
        var svc = new SettingsService(Path.Combine(_dir, "settings.json"));
        return svc;
    }

    public ScheduledPowerActionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = CreateSettings();
        // Start with clean slate.
        _settings.Current.AutoShutdown = new AutoShutdownSettings();
        _settings.Save();
    }

    [Fact]
    public void ScheduleAfter_PersistsRelativeSchedule()
    {
        _clock.Set(DateTime.UtcNow);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);

        var state = svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Sleep);

        Assert.True(state.Enabled);
        Assert.Equal(ScheduledPowerMode.Relative, state.Mode);
        Assert.Equal(ScheduledPowerActionType.Sleep, state.Action);
        Assert.NotNull(state.ExecuteAtUtc);
        Assert.Equal(30, state.DelayMinutes);
        Assert.True(state.RemainingSeconds > 0);

        // Reload settings and verify persistence.
        var reloaded = CreateSettings();
        Assert.True(reloaded.Current.AutoShutdown.Enabled);
        Assert.Equal(ScheduledPowerMode.Relative, reloaded.Current.AutoShutdown.Mode);
        Assert.Equal(ScheduledPowerActionType.Sleep, reloaded.Current.AutoShutdown.Action);
        Assert.NotNull(reloaded.Current.AutoShutdown.ExecuteAtUtc);
    }

    [Fact]
    public void ScheduleAfter_CalculatesExecuteAtUtc()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        _clock.Set(now);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);

        var state = svc.ScheduleAfter(TimeSpan.FromMinutes(45), ScheduledPowerActionType.Shutdown);

        Assert.Equal(now.AddMinutes(45), state.ExecuteAtUtc);
    }

    [Fact]
    public void ScheduleAfter_RejectsDelayBelowMinimum()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            svc.ScheduleAfter(TimeSpan.FromSeconds(30), ScheduledPowerActionType.Shutdown));
    }

    [Fact]
    public void ScheduleAfter_RejectsDelayAboveMaximum()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            svc.ScheduleAfter(TimeSpan.FromDays(8), ScheduledPowerActionType.Shutdown));
    }

    [Fact]
    public void ScheduleAfter_ReplacesExistingSchedule()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromMinutes(60), ScheduledPowerActionType.Sleep);
        Assert.Equal(ScheduledPowerActionType.Sleep, _settings.Current.AutoShutdown.Action);

        svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Shutdown);
        Assert.Equal(ScheduledPowerActionType.Shutdown, _settings.Current.AutoShutdown.Action);
        Assert.Equal(30, _settings.Current.AutoShutdown.DelayMinutes);
    }

    [Fact]
    public void Cancel_DisablesAndClearsRelativeSchedule()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromMinutes(60), ScheduledPowerActionType.Shutdown);

        var state = svc.Cancel();

        Assert.False(state.Enabled);
        Assert.Null(state.ExecuteAtUtc);
        Assert.Null(state.DelayMinutes);
        Assert.False(_settings.Current.AutoShutdown.Enabled);
    }

    [Fact]
    public void Start_RearmsFutureRelativeSchedule()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        _clock.Set(now);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromHours(2), ScheduledPowerActionType.Shutdown);

        // Simulate restart.
        _clock.Set(now.AddMinutes(30)); // 1.5 hours remaining.
        var svc2 = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc2.Start();

        var state = svc2.GetState();
        Assert.True(state.Enabled);
        Assert.True(state.RemainingSeconds > 0);
        Assert.True(state.RemainingSeconds <= 5400); // 1.5h in seconds.

        svc2.Dispose();
        svc.Dispose();
    }

    [Fact]
    public void Start_DiscardsExpiredRelativeSchedule()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        _clock.Set(now);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromHours(1), ScheduledPowerActionType.Shutdown);

        // Simulate restart after expiration.
        _clock.Set(now.AddHours(2)); // past expiry.
        var svc2 = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc2.Start();

        var state = svc2.GetState();
        Assert.False(state.Enabled);

        svc2.Dispose();
        svc.Dispose();
    }

    [Fact]
    public void ScheduleThenCancel_NoExecutionOccurs()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Shutdown);
        svc.Cancel();

        Assert.Empty(_executor.Executed);
    }

    [Fact]
    public void ExpiredSchedule_DiscardedOnRestart()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        _clock.Set(now);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Sleep);

        // Advance past expiry.
        _clock.Set(now.AddHours(1));
        var svc2 = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc2.Start();

        Assert.False(_settings.Current.AutoShutdown.Enabled);
        Assert.Null(_settings.Current.AutoShutdown.ExecuteAtUtc);

        svc2.Dispose();
        svc.Dispose();
    }

    [Fact]
    public void StateChanged_FiresAfterSchedule()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        ScheduledPowerActionState? received = null;
        svc.StateChanged += s => received = s;

        svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Shutdown);

        Assert.NotNull(received);
        Assert.True(received!.Enabled);
        Assert.Equal(ScheduledPowerActionType.Shutdown, received.Action);
    }

    [Fact]
    public void StateChanged_FiresAfterCancel()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Shutdown);

        ScheduledPowerActionState? received = null;
        svc.StateChanged += s => received = s;

        svc.Cancel();

        Assert.NotNull(received);
        Assert.False(received!.Enabled);
    }

    [Fact]
    public void DailySchedule_PersistsCorrectly()
    {
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        var time = new TimeOnly(22, 30);

        var state = svc.ScheduleDaily(time, ScheduledPowerActionType.Restart);

        Assert.True(state.Enabled);
        Assert.Equal(ScheduledPowerMode.Daily, state.Mode);
        Assert.Equal(ScheduledPowerActionType.Restart, state.Action);
        Assert.Equal("22:30", state.DailyTime);

        var reloaded = CreateSettings();
        Assert.True(reloaded.Current.AutoShutdown.Enabled);
        Assert.Equal(ScheduledPowerMode.Daily, reloaded.Current.AutoShutdown.Mode);
        Assert.Equal(ScheduledPowerActionType.Restart, reloaded.Current.AutoShutdown.Action);
        Assert.Equal("22:30", reloaded.Current.AutoShutdown.Time);

        svc.Dispose();
    }

    [Fact]
    public void GetState_ReturnsCorrectRemainingSeconds()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        _clock.Set(now);
        var svc = new ScheduledPowerActionService(_settings, _executor, _clock);
        svc.ScheduleAfter(TimeSpan.FromHours(1), ScheduledPowerActionType.Shutdown);

        _clock.Set(now.AddMinutes(10)); // 50 minutes remaining.

        var state = svc.GetState();
        Assert.True(state.RemainingSeconds > 2900); // ~50 min.
        Assert.True(state.RemainingSeconds <= 3000);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public sealed class FakePowerActionExecutor : IPowerActionExecutor
{
    public List<ScheduledPowerActionType> Executed { get; } = new();

    public void Execute(ScheduledPowerActionType action)
    {
        Executed.Add(action);
    }
}

public sealed class FakeClock : ISystemClock
{
    private DateTime _utcNow = DateTime.UtcNow;

    public DateTime UtcNow => _utcNow;

    public void Set(DateTime value) => _utcNow = value;
}
