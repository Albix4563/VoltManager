namespace VoltManager.Services;

internal sealed record ApplicationLifecycleActions(
    Action Attach,
    Action Detach,
    Action StartServices,
    Action StopServices,
    Func<CancellationToken, IDisposable> CreatePlanPollTimer,
    Func<CancellationToken, IDisposable> CreateBatteryHistoryTimer,
    Func<CancellationToken, IDisposable>? CreateResumeRecovery = null,
    Func<CancellationToken, IDisposable>? CreateResourceSelfMonitor = null);

public sealed class ApplicationLifecycleCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly ApplicationLifecycleActions _actions;
    private readonly RestartableLifecycle _lifecycle = new();
    private IDisposable? _planPollTimer;
    private IDisposable? _batteryHistoryTimer;
    private IDisposable? _resumeRecovery;
    private IDisposable? _resourceSelfMonitor;
    private bool _started;
    private bool _disposed;

    internal ApplicationLifecycleCoordinator(ApplicationLifecycleActions actions)
    {
        _actions = actions;
    }

    public void Start()
    {
        CancellationToken epoch;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ApplicationLifecycleCoordinator));
            if (_started)
                return;

            epoch = _lifecycle.Start();
            _started = true;
        }

        try
        {
            _actions.Attach();
            _actions.StartServices();

            IDisposable? resume = _actions.CreateResumeRecovery?.Invoke(epoch);
            IDisposable? resourceMonitor = _actions.CreateResourceSelfMonitor?.Invoke(epoch);
            IDisposable plan = _actions.CreatePlanPollTimer(epoch);
            IDisposable battery = _actions.CreateBatteryHistoryTimer(epoch);
            lock (_gate)
            {
                if (!_started || !_lifecycle.IsCurrent(epoch))
                {
                    SafeDispose(resume);
                    SafeDispose(resourceMonitor);
                    SafeDispose(plan);
                    SafeDispose(battery);
                    return;
                }
                _resumeRecovery = resume;
                _resourceSelfMonitor = resourceMonitor;
                _planPollTimer = plan;
                _batteryHistoryTimer = battery;
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        IDisposable? plan;
        IDisposable? battery;
        IDisposable? resume;
        IDisposable? resourceMonitor;
        lock (_gate)
        {
            if (!_started)
                return;
            _started = false;
            plan = _planPollTimer;
            battery = _batteryHistoryTimer;
            resume = _resumeRecovery;
            resourceMonitor = _resourceSelfMonitor;
            _planPollTimer = null;
            _batteryHistoryTimer = null;
            _resumeRecovery = null;
            _resourceSelfMonitor = null;
        }

        _lifecycle.Stop();
        SafeDispose(resume);
        SafeDispose(resourceMonitor);
        SafeDispose(plan);
        SafeDispose(battery);
        SafeInvoke(_actions.Detach, "application lifecycle detach");
        SafeInvoke(_actions.StopServices, "application lifecycle service stop");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
        _lifecycle.Dispose();
    }

    internal bool IsCurrent(CancellationToken token) => _lifecycle.IsCurrent(token);

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null) return;
        try { disposable.Dispose(); }
        catch (Exception ex) { Logger.Error("Lifecycle resource disposal failed", ex); }
    }

    private static void SafeInvoke(Action action, string description)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error(description + " failed", ex); }
    }
}
