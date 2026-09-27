namespace VoltManager.Services;

internal interface IResumeRecoveryScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class ThreadPoolResumeRecoveryScheduler : IResumeRecoveryScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            try { callback(); }
            finally { timer?.Dispose(); }
        }, null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

internal readonly record struct ResumeRecoveryStep(string Name, Action Action);

internal sealed class ResumeRecoveryCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ResumeRecoveryStep> _steps;
    private readonly IResumeRecoveryScheduler _scheduler;
    private readonly Action<Action> _queueWork;
    private readonly TimeSpan _debounce;
    private IDisposable? _pendingTimer;
    private long _timerGeneration;
    private bool _running;
    private bool _followUpPending;
    private bool _stopped;

    public ResumeRecoveryCoordinator(
        IReadOnlyList<ResumeRecoveryStep> steps,
        TimeSpan? debounce = null,
        IResumeRecoveryScheduler? scheduler = null,
        Action<Action>? queueWork = null)
    {
        _steps = steps;
        _debounce = debounce ?? TimeSpan.FromSeconds(3);
        _scheduler = scheduler ?? new ThreadPoolResumeRecoveryScheduler();
        _queueWork = queueWork ?? (work => _ = Task.Run(work));
    }

    public void SignalResume()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_running && _followUpPending)
                _followUpPending = false;
            ScheduleDebounceUnsafe();
        }
    }

    public void Stop()
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _followUpPending = false;
            timer = _pendingTimer;
            _pendingTimer = null;
            _timerGeneration++;
        }
        SafeDispose(timer);
    }

    public void Dispose() => Stop();

    private void ScheduleDebounceUnsafe()
    {
        IDisposable? previous = _pendingTimer;
        long generation = ++_timerGeneration;
        _pendingTimer = _scheduler.Schedule(_debounce, () => OnDebounceElapsed(generation));
        SafeDispose(previous);
    }

    private void OnDebounceElapsed(long generation)
    {
        bool queue = false;
        lock (_gate)
        {
            if (_stopped || generation != _timerGeneration) return;
            _pendingTimer = null;
            if (_running)
            {
                _followUpPending = true;
                return;
            }
            _running = true;
            queue = true;
        }

        if (queue)
            QueueRecovery();
    }

    private void QueueRecovery()
    {
        try { _queueWork(RunRecovery); }
        catch (Exception ex)
        {
            Logger.Error("Resume recovery dispatch failed", ex);
            CompleteRun();
        }
    }

    private void RunRecovery()
    {
        try
        {
            lock (_gate)
            {
                if (_stopped) return;
            }

            foreach (ResumeRecoveryStep step in _steps)
            {
                try { step.Action(); }
                catch (Exception ex) { Logger.Error("Resume recovery step failed: " + step.Name, ex); }
            }
        }
        finally
        {
            CompleteRun();
        }
    }

    private void CompleteRun()
    {
        bool queueFollowUp = false;
        lock (_gate)
        {
            _running = false;
            if (!_stopped && _followUpPending && _pendingTimer is null)
            {
                _followUpPending = false;
                _running = true;
                queueFollowUp = true;
            }
        }

        if (queueFollowUp)
            QueueRecovery();
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception ex) { Logger.Warn("Resume recovery timer disposal failed: " + ex.Message); }
    }
}
