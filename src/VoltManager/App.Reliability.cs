using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VoltManager.Reliability;
using VoltManager.Services;

namespace VoltManager;

public partial class App
{
    private int _fatalShutdownStarted;
    private int _crashDiagnosticCaptured;
    private System.Threading.Timer? _fatalExitTimer;

    public App()
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (SupervisorBootstrap.TryDelegate(arguments))
            Environment.Exit(AppExitCodes.Success);

        // App.OnStartup installs the legacy handlers before calling base.OnStartup.
        // Startup is raised by that base call, so this handler is appended last and
        // turns an otherwise swallowed Dispatcher exception into a bounded fatal exit.
        Startup += AttachReliabilityHandlers;
        Exit += OnReliabilityExit;
        AppDomain.CurrentDomain.UnhandledException += OnReliabilityDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnReliabilityUnobservedTaskException;
    }

    private void AttachReliabilityHandlers(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnReliabilityDispatcherUnhandledException;
    }

    private void OnReliabilityDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CaptureCrashOnce("unhandled_ui_exception", e.Exception, AppExitCodes.UnhandledUiException);
        BeginBoundedFatalShutdown(AppExitCodes.UnhandledUiException);
    }

    private void OnReliabilityDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        CaptureCrashOnce(
            "unhandled_appdomain_exception",
            e.ExceptionObject as Exception,
            AppExitCodes.UnhandledUiException);
    }

    private void OnReliabilityUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // This is diagnostic only: an unobserved task is not necessarily process-fatal.
        // The existing handler records the full exception and marks it observed.
        CrashDiagnostics.Capture("unobserved_task_exception", e.Exception, AppExitCodes.Success);
    }

    private void OnReliabilityExit(object sender, ExitEventArgs e)
    {
        if (e.ApplicationExitCode != AppExitCodes.Success)
            CaptureCrashOnce("abnormal_application_exit", null, e.ApplicationExitCode);
    }

    private void CaptureCrashOnce(string category, Exception? exception, int exitCode)
    {
        if (Interlocked.Exchange(ref _crashDiagnosticCaptured, 1) != 0)
            return;

        string? path = CrashDiagnostics.Capture(category, exception, exitCode);
        if (!string.IsNullOrWhiteSpace(path))
            Logger.Info("Crash diagnostic saved: " + path);
    }

    private void BeginBoundedFatalShutdown(int exitCode)
    {
        if (Interlocked.Exchange(ref _fatalShutdownStarted, 1) != 0)
            return;

        var steps = new[]
        {
            new CleanupStep("scheduled power action service", () => ScheduledPowerActions?.Dispose()),
            new CleanupStep("metrics handler", () =>
            {
                if (Monitor != null)
                    Monitor.MetricsUpdated -= OnMetricsSampled;
            }),
            new CleanupStep("plan poll timer", () => _planPollTimer?.Dispose()),
            new CleanupStep("battery history timer", () => _batteryHistoryTimer?.Dispose()),
            new CleanupStep("monitor", () => Monitor?.Dispose()),
            new CleanupStep("heavy apps", () => HeavyApps?.Dispose()),
            new CleanupStep("app profiles", () => AppProfiles?.Dispose()),
            new CleanupStep("keep awake", () => Awake?.Dispose()),
            new CleanupStep("standby cleaner", () => StandbyAutoCleaner?.Dispose()),
            new CleanupStep("theme", () => Theme?.Dispose()),
            new CleanupStep("widgets", () => Widgets?.Dispose()),
            new CleanupStep("remote commands", () => _remoteCommands?.Dispose()),
            new CleanupStep("show wait", () => _showWait?.Unregister(null)),
            new CleanupStep("show event", () => _showEvent?.Dispose()),
            new CleanupStep("mutex", ReleaseApplicationMutex),
        };

        IReadOnlyList<CleanupStepResult> results = BoundedCleanup.Run(
            steps,
            totalTimeout: TimeSpan.FromSeconds(8),
            maximumPerStep: TimeSpan.FromSeconds(1));

        foreach (CleanupStepResult result in results.Where(result => result.Outcome != CleanupOutcome.Completed))
        {
            Logger.Warn($"Fatal cleanup {result.Outcome}: {result.Name} ({result.ExceptionType ?? "no exception"})");
        }

        // If a foreground thread or native component prevents WPF shutdown, force the
        // documented exit code after the graceful window instead of hanging forever.
        _fatalExitTimer = new System.Threading.Timer(
            _ => Environment.Exit(exitCode),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        Shutdown(exitCode);
    }

    private void ReleaseApplicationMutex()
    {
        if (_mutex == null)
            return;

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
        _mutex = null;
    }
}
