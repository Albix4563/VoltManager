using System;
using System.Threading;
using VoltManager.Models;

namespace VoltManager.Services;

public class StandbyAutoCleanerService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<MemoryStatus> _memoryStatusReader;
    private readonly Func<bool> _standbyPurger;
    private readonly object _lock = new();
    
    private Timer? _timer;

    public event Action<MemoryStatus>? AutoCleaned;

    public StandbyAutoCleanerService(
        SettingsService settings,
        Func<MemoryStatus>? memoryStatusReader = null,
        Func<bool>? standbyPurger = null)
    {
        _settings = settings;
        _memoryStatusReader = memoryStatusReader ?? (() => new MemoryOptimizerService().GetMemoryStatus());
        _standbyPurger = standbyPurger ?? (() => new MemoryOptimizerService().PurgeStandbyList());
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

    public bool PurgeManual()
    {
        lock (_lock)
        {
            bool success = _standbyPurger();
            if (success)
            {
                var config = _settings.Current.StandbyAutoCleaner;
                config.LastPurgedUtc = DateTime.UtcNow;
                _settings.Save();
            }
            return success;
        }
    }

    private void Tick(object? state)
    {
        CheckAndClean();
    }

    public void CheckAndClean()
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
                return;
            }

            var mem = _memoryStatusReader();
            var now = DateTime.UtcNow;
            bool shouldPurge = false;

            if (mem.StandbyGb >= config.ThresholdGb)
            {
                shouldPurge = true;
            }
            else if (config.LastPurgedUtc == null || (now - config.LastPurgedUtc.Value).TotalMinutes >= config.IntervalMinutes)
            {
                shouldPurge = true;
            }

            if (shouldPurge)
            {
                bool success = _standbyPurger();
                if (success)
                {
                    config.LastPurgedUtc = DateTime.UtcNow;
                    _settings.Save();

                    var freshMem = _memoryStatusReader();
                    AutoCleaned?.Invoke(freshMem);
                }
            }
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
