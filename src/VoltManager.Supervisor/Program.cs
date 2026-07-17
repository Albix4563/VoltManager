namespace VoltManager.Supervisor;

internal static class Program
{
    private static readonly TimeSpan ChildShutdownTimeout = TimeSpan.FromSeconds(8);

    [STAThread]
    private static int Main(string[] args)
    {
        SupervisorPaths paths = SupervisorPaths.CreateDefault();
        var events = new JsonSupervisorEventSink(paths.EventLogFile);

        if (!SupervisorOptions.TryParse(args, out SupervisorOptions? options, out string error) || options == null)
        {
            events.Write("invalid_supervisor_arguments", new { error });
            return SupervisorExitCodes.InvalidArguments;
        }

        using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(SupervisorNames.SupervisorMutex);
        if (guard == null)
        {
            events.Write("duplicate_supervisor_rejected");
            return SupervisorExitCodes.Success;
        }

        using var wait = new WakeWaitStrategy(SupervisorNames.WakeEvent);
        var processFactory = new ProcessChildFactory();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => processFactory.StopCurrent(ChildShutdownTimeout);

        var stateStore = new FileSupervisorStateStore(paths.StateFile, events);
        var engine = new SupervisorEngine(
            new SystemClock(),
            new CryptoJitterSource(),
            wait,
            stateStore,
            events,
            processFactory,
            new RestartPolicy(RestartPolicyOptions.Default));

        events.Write("supervisor_started", new
        {
            childFile = Path.GetFileName(options.ChildPath),
            policy = new
            {
                initialDelayMs = (long)RestartPolicyOptions.Default.InitialDelay.TotalMilliseconds,
                maximumDelayMs = (long)RestartPolicyOptions.Default.MaximumDelay.TotalMilliseconds,
                RestartPolicyOptions.Default.JitterRatio,
                RestartPolicyOptions.Default.MaximumRestarts,
                attemptWindowSeconds = (long)RestartPolicyOptions.Default.AttemptWindow.TotalSeconds,
                stablePeriodSeconds = (long)RestartPolicyOptions.Default.StablePeriod.TotalSeconds,
            },
        });

        int result = engine.Run(options);
        events.Write("supervisor_stopped", new { exitCode = result });
        return result;
    }
}
