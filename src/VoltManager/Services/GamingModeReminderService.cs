namespace VoltManager.Services;

public enum GamingModeReminderDecision
{
    None,
    Prompt
}

/// <summary>
/// Tracks the explicit tray "Piano gaming" session and decides when to remind the user
/// that the high-performance plan is still locked while CPU load is idle.
/// </summary>
public sealed class GamingModeReminderService
{
    private readonly object _gate = new();
    private readonly double _idleCpuThresholdPct;
    private readonly TimeSpan _idleDurationBeforeReminder;
    private readonly TimeSpan _repeatReminderInterval;
    private DateTime? _idleSinceUtc;
    private DateTime _nextReminderAllowedUtc;

    public GamingModeReminderService(
        double idleCpuThresholdPct = 10,
        TimeSpan? idleDurationBeforeReminder = null,
        TimeSpan? repeatReminderInterval = null)
    {
        _idleCpuThresholdPct = idleCpuThresholdPct;
        _idleDurationBeforeReminder = idleDurationBeforeReminder ?? TimeSpan.FromMinutes(10);
        _repeatReminderInterval = repeatReminderInterval ?? TimeSpan.FromMinutes(20);
    }

    public bool Active
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    private bool _active;

    public void Start(DateTime nowUtc)
    {
        lock (_gate)
        {
            _active = true;
            _idleSinceUtc = null;
            _nextReminderAllowedUtc = nowUtc.Add(_idleDurationBeforeReminder);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _idleSinceUtc = null;
            _nextReminderAllowedUtc = DateTime.MinValue;
        }
    }

    public GamingModeReminderDecision ObserveCpu(double cpuPct, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_active)
                return GamingModeReminderDecision.None;

            if (cpuPct >= _idleCpuThresholdPct)
            {
                _idleSinceUtc = null;
                _nextReminderAllowedUtc = nowUtc.Add(_idleDurationBeforeReminder);
                return GamingModeReminderDecision.None;
            }

            _idleSinceUtc ??= nowUtc;
            if (nowUtc - _idleSinceUtc < _idleDurationBeforeReminder)
                return GamingModeReminderDecision.None;

            if (nowUtc < _nextReminderAllowedUtc)
                return GamingModeReminderDecision.None;

            _nextReminderAllowedUtc = nowUtc.Add(_repeatReminderInterval);
            return GamingModeReminderDecision.Prompt;
        }
    }
}
