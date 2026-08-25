using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VoltManager.Reliability;
using VoltManager.Services;

namespace VoltManager;

public partial class App
{
    private static readonly TimeSpan FatalShutdownTimeout = TimeSpan.FromSeconds(8);
    private int _fatalShutdownStarted;
    private int _crashDiagnosticCaptured;
    private System.Threading.Timer? _fatalExitTimer;

    public App()
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (SupervisorBootstrap.TryDelegate(arguments))
            Environment.Exit(AppExitCodes.Success);

        // Sole UI-thread unhandled policy (see UnhandledExceptionPolicy.UiThreadPolicy).
        // Do not also register a keep-alive MessageBox handler for the same event.
        DispatcherUnhandledException += OnReliabilityDispatcherUnhandledException;
        Exit += OnReliabilityExit;
        AppDomain.CurrentDomain.UnhandledException += OnReliabilityDomainUnhandledException;

        // App.OnStartup owns startup directly rather than relying on the Startup event.
        // Queue adaptive initialization on the dispatcher so StartupCore has synchronously
        // created Monitor/HeavyApps/MainWindow before this runs.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(InitializeAdaptiveResourceManagement));
        Exit += OnAdaptiveResourceExit;
    }

    private void OnReliabilityDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark handled so WPF does not rethrow while we shut down on our terms.
        e.Handled = true;

        var action = UnhandledExceptionPolicy.UiThreadPolicy;
        try { Logger.Error("Unhandled UI-thread exception", e.Exception); }
        catch { /* logging must not mask the original failure */ }

        if (UnhandledExceptionPolicy.CapturesCrashDiagnostic(action))
            CaptureCrashOnce("unhandled_ui_exception", e.Exception, AppExitCodes.UnhandledUiException);

        if (UnhandledExceptionPolicy.BeginsFatalShutdown(action))
            BeginBoundedFatalShutdown(AppExitCodes.UnhandledUiException);
    }

    private void OnReliabilityDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        CaptureCrashOnce(
            "unhandled_appdomain_exception",
            e.ExceptionObject as Exception,
            AppExitCodes.UnhandledUiException);
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

        // Arm the hard deadline before any cleanup. This also bounds WPF-affine
        // teardown that cannot safely be moved to a worker thread.
        _fatalExitTimer = new System.Threading.Timer(
            _ => Environment.Exit(exitCode),
            null,
            FatalShutdownTimeout,
            Timeout.InfiniteTimeSpan);

        // Widget windows and the application mutex are dispatcher/thread-affine.
        // Run them on the owning UI thread; the timer above remains the hard limit.
        TryInlineCleanup("widgets", () => Widgets?.Dispose());
        TryInlineCleanup("mutex", ReleaseApplicationMutex);

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
            new CleanupStep("hardware access", () => HardwareAccess?.Dispose()),
            new CleanupStep("heavy apps", () => HeavyApps?.Dispose()),
            new CleanupStep("app profiles", () => AppProfiles?.Dispose()),
            new CleanupStep("keep awake", () => Awake?.Dispose()),
            new CleanupStep("standby cleaner", () => StandbyAutoCleaner?.Dispose()),
            new CleanupStep("remote commands", () => _remoteCommands?.Dispose()),
            new CleanupStep("show wait", () => _showWait?.Unregister(null)),
            new CleanupStep("show event", () => _showEvent?.Dispose()),
        };

        IReadOnlyList<CleanupStepResult> results = BoundedCleanup.Run(
            steps,
            totalTimeout: TimeSpan.FromSeconds(6),
            maximumPerStep: TimeSpan.FromSeconds(1));

        foreach (CleanupStepResult result in results.Where(result => result.Outcome != CleanupOutcome.Completed))
        {
            Logger.Warn($"Fatal cleanup {result.Outcome}: {result.Name} ({result.ExceptionType ?? "no exception"})");
        }

        Shutdown(exitCode);
    }

    private static void TryInlineCleanup(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Warn($"Fatal cleanup failed: {name} ({ex.GetType().FullName})"); }
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
