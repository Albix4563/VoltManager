using System.Diagnostics;

namespace VoltManager.Supervisor;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}

public sealed class WakeWaitStrategy : IWaitStrategy
{
    private readonly EventWaitHandle _wakeEvent;

    public WakeWaitStrategy(string eventName)
    {
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public bool Wait(TimeSpan delay) => _wakeEvent.WaitOne(delay);

    public void Dispose() => _wakeEvent.Dispose();
}

public sealed class ProcessChildFactory : IChildProcessFactory
{
    private readonly object _gate = new();
    private Process? _current;

    public IChildProcess Start(string childPath, IReadOnlyList<string> childArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = childPath,
            WorkingDirectory = Path.GetDirectoryName(childPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--supervised");
        foreach (string argument in childArguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        lock (_gate)
            _current = process;

        return new TrackingProcessChild(process, () =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, process))
                    _current = null;
            }
        });
    }

    public void StopCurrent(TimeSpan gracefulTimeout)
    {
        Process? process;
        lock (_gate)
            process = _current;

        if (process == null)
            return;

        try
        {
            if (process.HasExited)
                return;

            bool closeRequested = process.CloseMainWindow();
            if (closeRequested && process.WaitForExit((int)Math.Clamp(gracefulTimeout.TotalMilliseconds, 0, int.MaxValue)))
                return;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch
        {
            // Process termination during OS shutdown is best effort.
        }
    }

    private sealed class TrackingProcessChild : IChildProcess
    {
        private readonly Process _process;
        private readonly Action _onDispose;

        public TrackingProcessChild(Process process, Action onDispose)
        {
            _process = process;
            _onDispose = onDispose;
        }

        public int Id => _process.Id;

        public int WaitForExit()
        {
            _process.WaitForExit();
            return _process.ExitCode;
        }

        public void Dispose()
        {
            _onDispose();
            _process.Dispose();
        }
    }
}

public sealed class SupervisorEngine
{
    private readonly IClock _clock;
    private readonly IJitterSource _jitter;
    private readonly IWaitStrategy _wait;
    private readonly ISupervisorStateStore _stateStore;
    private readonly ISupervisorEventSink _events;
    private readonly IChildProcessFactory _processes;
    private readonly RestartPolicy _policy;

    public SupervisorEngine(
        IClock clock,
        IJitterSource jitter,
        IWaitStrategy wait,
        ISupervisorStateStore stateStore,
        ISupervisorEventSink events,
        IChildProcessFactory processes,
        RestartPolicy policy)
    {
        _clock = clock;
        _jitter = jitter;
        _wait = wait;
        _stateStore = stateStore;
        _events = events;
        _processes = processes;
        _policy = policy;
    }

    public int Run(SupervisorOptions options)
    {
        if (options.ResetState)
            _stateStore.Reset();

        SupervisorState state = _stateStore.Load();
        string currentVersion = ReadChildVersion(options.ChildPath);
        if (!string.Equals(state.ChildVersion, currentVersion, StringComparison.Ordinal))
        {
            state.Reset(currentVersion);
            _stateStore.Save(state);
            _events.Write("restart_state_reset_for_version", new { childVersion = currentVersion });
        }

        DateTimeOffset initialNow = _clock.UtcNow;
        if (_policy.IsBlocked(state, initialNow))
        {
            _events.Write("crash_loop_blocked", new { state.BlockedUntilUtc, failures = state.CrashTimesUtc.Count });
            return SupervisorExitCodes.CrashLoopBlocked;
        }

        while (true)
        {
            DateTimeOffset startedAt = _clock.UtcNow;
            int exitCode;
            int childPid = 0;

            try
            {
                _events.Write("child_starting", new { failuresInWindow = state.CrashTimesUtc.Count });
                using IChildProcess child = _processes.Start(options.ChildPath, options.ChildArguments);
                childPid = child.Id;
                _events.Write("child_started", new { childPid });
                exitCode = child.WaitForExit();
            }
            catch (Exception ex)
            {
                exitCode = SupervisorExitCodes.ChildStartFailed;
                _events.Write("child_start_failed", new { exceptionType = ex.GetType().FullName });
            }

            DateTimeOffset endedAt = _clock.UtcNow;
            TimeSpan uptime = endedAt - startedAt;

            if (exitCode == 0)
            {
                state.Reset(currentVersion);
                _stateStore.Save(state);
                _events.Write("child_exited_normally", new { childPid, uptimeMs = (long)uptime.TotalMilliseconds });
                return SupervisorExitCodes.Success;
            }

            _events.Write("child_exited_abnormally", new
            {
                childPid,
                exitCode,
                uptimeMs = (long)uptime.TotalMilliseconds,
            });

            RestartDecision decision = _policy.RegisterFailure(state, endedAt, uptime, _jitter);
            _stateStore.Save(state);

            if (decision.StableCounterReset)
                _events.Write("restart_counter_reset_after_stable_run", new { uptimeMs = (long)uptime.TotalMilliseconds });

            if (!decision.ShouldRestart)
            {
                _events.Write("restart_budget_exhausted", new
                {
                    failures = decision.Attempt,
                    decision.BlockedUntilUtc,
                });
                return SupervisorExitCodes.CrashLoopBlocked;
            }

            _events.Write("restart_scheduled", new
            {
                attempt = decision.Attempt,
                delayMs = (long)decision.Delay.TotalMilliseconds,
                exitCode,
            });

            bool manuallyWoken = _wait.Wait(decision.Delay);
            if (manuallyWoken)
                _events.Write("restart_delay_interrupted_by_user_launch", new { attempt = decision.Attempt });
        }
    }

    private static string ReadChildVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion ?? File.GetLastWriteTimeUtc(path).Ticks.ToString();
        }
        catch
        {
            return File.GetLastWriteTimeUtc(path).Ticks.ToString();
        }
    }
}
