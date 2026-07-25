using System.Globalization;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class ScheduledPowerActionService : IDisposable
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(7);

    private readonly SettingsService _settings;
    private readonly IPowerActionExecutor _executor;
    private readonly ISystemClock _clock;
    private readonly object _sync = new();

    private System.Threading.Timer? _relativeTimer;
    private System.Threading.Timer? _dailyTimer;
    private long _generation;

    public event Action<ScheduledPowerActionState>? StateChanged;

    public ScheduledPowerActionService(SettingsService settings, IPowerActionExecutor executor, ISystemClock clock)
    {
        _settings = settings;
        _executor = executor;
        _clock = clock;
    }

    public ScheduledPowerActionState GetState()
    {
        lock (_sync)
        {
            return CreateStateUnsafe();
        }
    }

    public ScheduledPowerActionState ScheduleAfter(TimeSpan delay, ScheduledPowerActionType action)
    {
        ValidateDelay(delay);

        ScheduledPowerActionState state;

        lock (_sync)
        {
            CancelTimersUnsafe();

            DateTime now = _clock.UtcNow;
            DateTime executeAt = now.Add(delay);

            var config = _settings.Current.AutoShutdown;
            config.Enabled = true;
            config.Mode = ScheduledPowerMode.Relative;
            config.Action = action;
            config.CreatedAtUtc = now;
            config.ExecuteAtUtc = executeAt;
            config.DelayMinutes = (int)Math.Ceiling(delay.TotalMinutes);
            config.LastTriggeredLocalDate = null;

            _settings.Save();

            long generation = ++_generation;

            _relativeTimer = new System.Threading.Timer(
                _ => ExecuteRelativeCallback(generation),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            state = CreateStateUnsafe();

            Logger.Info(
                $"Scheduled action created: action={action}, " +
                $"executeAtUtc={executeAt:O}, " +
                $"delayMinutes={delay.TotalMinutes:F0}");
        }

        PublishState(state);
        return state;
    }

    public ScheduledPowerActionState ScheduleDaily(TimeOnly time, ScheduledPowerActionType action)
    {
        ScheduledPowerActionState state;

        lock (_sync)
        {
            CancelTimersUnsafe();

            var config = _settings.Current.AutoShutdown;
            config.Enabled = true;
            config.Mode = ScheduledPowerMode.Daily;
            config.Action = action;
            config.Time = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            config.ExecuteAtUtc = null;
            config.DelayMinutes = null;
            config.CreatedAtUtc = null;
            config.LastTriggeredLocalDate = null;

            _settings.Save();

            StartDailyTimerUnsafe();

            state = CreateStateUnsafe();

            Logger.Info($"Daily schedule created: action={action}, time={config.Time}");
        }

        PublishState(state);
        return state;
    }

    public ScheduledPowerActionState Cancel()
    {
        ScheduledPowerActionState state;

        lock (_sync)
        {
            CancelTimersUnsafe();

            var config = _settings.Current.AutoShutdown;
            var action = config.Action;
            config.Enabled = false;
            config.Mode = ScheduledPowerMode.Daily;
            config.ExecuteAtUtc = null;
            config.DelayMinutes = null;
            config.CreatedAtUtc = null;

            _settings.Save();

            ++_generation;
            state = CreateStateUnsafe();

            Logger.Info($"Scheduled action cancelled: action={action}");
        }

        PublishState(state);
        return state;
    }

    public void Start()
    {
        lock (_sync)
        {
            var config = _settings.Current.AutoShutdown;

            if (!config.Enabled)
                return;

            if (config.Mode == ScheduledPowerMode.Relative)
                RestoreRelativeScheduleUnsafe(config);
            else
                StartDailyTimerUnsafe();
        }

        PublishState(GetState());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CancelTimersUnsafe();
        }
    }

    private void RestoreRelativeScheduleUnsafe(AutoShutdownSettings config)
    {
        if (config.ExecuteAtUtc is not DateTime executeAt)
        {
            DisableInvalidScheduleUnsafe();
            Logger.Warn("Relative schedule without ExecuteAtUtc; disabling.");
            return;
        }

        TimeSpan remaining = executeAt - _clock.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            DisableInvalidScheduleUnsafe();
            Logger.Warn($"Expired scheduled action discarded: executeAtUtc={executeAt:O}");
            return;
        }

        long generation = ++_generation;

        _relativeTimer = new System.Threading.Timer(
            _ => ExecuteRelativeCallback(generation),
            null,
            remaining,
            Timeout.InfiniteTimeSpan);

        Logger.Info($"Restored relative schedule: executeAtUtc={executeAt:O}, remaining={remaining.TotalMinutes:F0}min");
    }

    private void DisableInvalidScheduleUnsafe()
    {
        var config = _settings.Current.AutoShutdown;
        config.Enabled = false;
        config.ExecuteAtUtc = null;
        config.DelayMinutes = null;
        config.CreatedAtUtc = null;
        config.Mode = ScheduledPowerMode.Daily;
        _settings.Save();
        ++_generation;
    }

    private void ExecuteRelativeCallback(long generation)
    {
        ScheduledPowerActionType action;

        lock (_sync)
        {
            if (generation != _generation)
                return;

            var config = _settings.Current.AutoShutdown;

            if (!config.Enabled ||
                config.Mode != ScheduledPowerMode.Relative)
                return;

            action = config.Action;

            config.Enabled = false;
            config.ExecuteAtUtc = null;
            config.DelayMinutes = null;
            config.CreatedAtUtc = null;

            _settings.Save();

            _relativeTimer?.Dispose();
            _relativeTimer = null;

            ++_generation;
        }

        Logger.Info($"Executing scheduled action: action={action}");

        PublishState(GetState());

        try
        {
            _executor.Execute(action);
        }
        catch (Exception ex)
        {
            Logger.Error("Scheduled power action failed", ex);
        }
    }

    private void StartDailyTimerUnsafe()
    {
        _dailyTimer?.Dispose();
        _dailyTimer = new System.Threading.Timer(
            _ => DailyCheckCallback(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15));
    }

    private void DailyCheckCallback()
    {
        lock (_sync)
        {
            var scheduled = _settings.Current.AutoShutdown;
            if (scheduled is not { Enabled: true }) return;
            if (scheduled.Mode != ScheduledPowerMode.Daily) return;
            if (!TimeOnly.TryParseExact(scheduled.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var scheduledTime)) return;

            var now = DateTime.Now;
            if (now.Hour != scheduledTime.Hour || now.Minute != scheduledTime.Minute) return;

            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(scheduled.LastTriggeredLocalDate, today, StringComparison.Ordinal)) return;

            scheduled.LastTriggeredLocalDate = today;
            _settings.Save();

            Logger.Info($"Executing daily action: action={scheduled.Action}, time={scheduled.Time}");

            PublishState(CreateStateUnsafe());

            try
            {
                _executor.Execute(scheduled.Action);
            }
            catch (Exception ex)
            {
                Logger.Error("Daily scheduled power action failed", ex);
            }
        }
    }

    private void CancelTimersUnsafe()
    {
        _relativeTimer?.Dispose();
        _relativeTimer = null;

        _dailyTimer?.Dispose();
        _dailyTimer = null;
    }

    private ScheduledPowerActionState CreateStateUnsafe()
    {
        var config = _settings.Current.AutoShutdown;

        long remainingSeconds = 0;
        if (config.Enabled && config.Mode == ScheduledPowerMode.Relative && config.ExecuteAtUtc is DateTime executeAt)
        {
            remainingSeconds = (long)(executeAt - _clock.UtcNow).TotalSeconds;
            if (remainingSeconds < 0) remainingSeconds = 0;
        }

        bool expired = config.Enabled &&
                       config.Mode == ScheduledPowerMode.Relative &&
                       config.ExecuteAtUtc is DateTime execAt &&
                       execAt <= _clock.UtcNow;

        return new ScheduledPowerActionState
        {
            Enabled = config.Enabled,
            Mode = config.Mode,
            Action = config.Action,
            ExecuteAtUtc = config.ExecuteAtUtc,
            DelayMinutes = config.DelayMinutes,
            RemainingSeconds = remainingSeconds,
            DailyTime = config.Mode == ScheduledPowerMode.Daily ? config.Time : null,
            Expired = expired,
        };
    }

    private void PublishState(ScheduledPowerActionState state)
    {
        // Fire outside lock to avoid deadlock with subscribers.
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { Logger.Error("StateChanged subscriber failed", ex); }
    }

    private static void ValidateDelay(TimeSpan delay)
    {
        if (delay < MinDelay || delay > MaxDelay)
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                "Delay must be between 1 minute and 7 days.");
    }
}
