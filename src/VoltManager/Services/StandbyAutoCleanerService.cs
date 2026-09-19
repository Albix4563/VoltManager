using System;
using System.Threading;
using VoltManager.Models;

namespace VoltManager.Services;

public class StandbyAutoCleanerService : IDisposable
{
    internal const double PressureThresholdPct = 92;
    internal static readonly TimeSpan PressureHold = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly Func<MemoryStatus> _memoryStatusReader;
    private readonly Func<bool> _standbyPurger;
    private readonly Func<bool> _protectedWorkloadActive;
    private readonly object _lock = new();
    private DateTime? _pressureSinceUtc;
    private Timer? _timer;

    public event Action<MemoryStatus>? AutoCleaned;

    public StandbyAutoCleanerService(
        SettingsService settings,
        Func<MemoryStatus>? memoryStatusReader = null,
        Func<bool>? standbyPurger = null,
        Func<bool>? protectedWorkloadActive = null)
    {
        _settings = settings;
        _memoryStatusReader = memoryStatusReader ?? (() => new MemoryOptimizerService().GetMemoryStatus());
        _standbyPurger = standbyPurger ?? (() => new MemoryOptimizerService().PurgeStandbyList());
        _protectedWorkloadActive = protectedWorkloadActive ?? (() => false);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_timer == null)
            {
                _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>Starts the clean-up loop after <paramref name="delay"/> to
    /// reduce contention with other startup work.</summary>
    public void StartDelayed(TimeSpan delay)
    {
        lock (_lock)
        {
            if (_timer == null)
            {
                _timer = new Timer(Tick, null, delay, TimeSpan.FromSeconds(30));
            }
        }
    }

    public bool PurgeManual()
    {
        lock (_lock)
        {
            bool success = _standbyPurger();
            if (success)
            {
                DateTime purgedAt = DateTime.UtcNow;
                _settings.Update(state => state.StandbyAutoCleaner.LastPurgedUtc = purgedAt);
            }
            return success;
        }
    }

    public void ResetAutomaticCandidate()
    {
        lock (_lock) _pressureSinceUtc = null;
    }

    private void Tick(object? state)
    {
        CheckAndClean();
    }

    public void CheckAndClean(DateTime? nowUtc = null)
    {
        if (!Monitor.TryEnter(_lock))
        {
            return;
        }

        try
        {
            var config = _settings.Current.StandbyAutoCleaner;
            if (config == null || !config.Enabled)
            {
                _pressureSinceUtc = null;
                return;
            }

            if (_protectedWorkloadActive())
            {
                _pressureSinceUtc = null;
                return;
            }

            var mem = _memoryStatusReader();
            var now = nowUtc ?? DateTime.UtcNow;
            if (mem.InUsePct < PressureThresholdPct)
            {
                _pressureSinceUtc = null;
                return;
            }

            _pressureSinceUtc ??= now;
            if (now - _pressureSinceUtc < PressureHold) return;
            if (mem.StandbyGb < config.ThresholdGb) return;
            if (config.LastPurgedUtc is DateTime last && now - last < TimeSpan.FromMinutes(config.IntervalMinutes)) return;

            // A session can start while a slow memory query is in flight.
            if (_protectedWorkloadActive())
            {
                _pressureSinceUtc = null;
                return;
            }

            if (!_standbyPurger()) return;

            _settings.Update(state => state.StandbyAutoCleaner.LastPurgedUtc = now);
            _pressureSinceUtc = null;
            AutoCleaned?.Invoke(_memoryStatusReader());
        }
        catch
        {
            // Background ticks must never crash the host application.
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
