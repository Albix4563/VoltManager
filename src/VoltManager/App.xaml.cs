using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using VoltManager.Bridge;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Reliability;
using VoltManager.Services;

namespace VoltManager;

public partial class App : Application
{
    private static string MutexName => ValidationEnvironment.NamedObject("VoltManager_SingleInstance_Mutex");
    private static string ShowEventName => ValidationEnvironment.NamedObject("VoltManager_ShowWindow_Event");

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private RemoteCommandService? _remoteCommands;
    private ApplicationLifecycleCoordinator? _applicationLifecycle;
    public AppServiceGraph Services { get; private set; } = null!;
    private int _exitStarted;
    private int _serviceDisposalStarted;

    public HardwareInfoService Hardware { get; private set; } = null!;
    public SettingsService Settings { get; private set; } = null!;
    public PowerPlanService Power { get; private set; } = null!;
    public PowerAwakeService Awake { get; private set; } = null!;
    public IHardwareAccess HardwareAccess { get; private set; } = null!;
    public MonitorService Monitor { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public UpdateCoordinator UpdateCoordinator { get; private set; } = null!;
    public StartupService AutoStart { get; private set; } = null!;
    public AutomationEngine Automation { get; private set; } = null!;
    public HeavyAppDetectionService HeavyApps { get; private set; } = null!;
    public ProtectedFullscreenCoverageService FullscreenCoverage { get; private set; } = null!;
    public AppPowerProfileService AppProfiles { get; private set; } = null!;
    public PowerSourcePlanService PowerSourcePlans { get; private set; } = null!;
    public ThermalGuardService ThermalGuard { get; private set; } = null!;
    public IdlePowerGuardService IdlePowerGuard { get; private set; } = null!;
    public PowerRequestCoordinator PowerRequests { get; private set; } = null!;
    public StandbyAutoCleanerService StandbyAutoCleaner { get; private set; } = null!;
    public BatteryHistoryService BatteryHistory { get; private set; } = null!;
    public ThemeService Theme { get; private set; } = null!;
    public LocalizationService Loc { get; private set; } = null!;
    public WidgetManager Widgets { get; private set; } = null!;
    public ScheduledPowerActionService ScheduledPowerActions { get; private set; } = null!;
    private Task<CoreWebView2Environment>? _webViewEnvironment;
    // Lazy: tray-only sessions never spin up Chromium until the UI or a widget needs it.
    public Task<CoreWebView2Environment> WebViewEnvironment
        => _webViewEnvironment ??= CreateWebViewEnvironmentAsync();

    private PowerFlowService _powerFlow = null!;
    private MainWindow? _mainWindow;

    public PowerPlan? ActivePlan { get; private set; }
    public CpuAutomationState CpuAutomationState { get; private set; } = new();
    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;
    public event Action<CpuAutomationState>? CpuAutomationStateChanged;
    public event Action<ActivePlanReasonState>? ActivePlanReasonChanged;
    public event Action<PowerPlanConflictNotification>? PowerPlanConflictDetected;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Init logging + global handlers first so anything below is captured.
        Logger.Init();
        HookGlobalExceptionHandlers();

        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            // Startup failure leaves no usable app; log, tell the user where the
            // log is, and shut down cleanly instead of dying with a raw crash.
            Logger.Error("Fatal error during startup", ex);
            try
            {
                var fallbackLoc = new LocalizationService();
                try { fallbackLoc.Initialize(new AppSettings()); } catch { }
                MessageBox.Show(
                    fallbackLoc.T("Dialog_StartupFailed",
                        Logger.LogFilePath ?? fallbackLoc.T("UpdatePrompt_ND"),
                        ex.Message),
                    fallbackLoc.T("Dialog_VoltManagerTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* never let the dialog mask the original failure */ }
            Shutdown(AppExitCodes.StartupFailure);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        // Cumulative marks (one batched log after Show) — cold-path cost of each stage.
        var sw = Stopwatch.StartNew();
        var marks = new List<(string Name, long Ms)>(8);
        void Mark(string name) => marks.Add((name, sw.ElapsedMilliseconds));

        string? startupCommand = RemoteCommandProtocol.ParseCommandArg(e.Args);

        _mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            // Another instance running: forward the command if any,
            // otherwise signal it to show its window, then quit.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(startupCommand != null
                    ? RemoteCommandProtocol.EventName(startupCommand)
                    : ShowEventName);
                evt.Set();
            }
            catch (Exception ex) { Logger.Warn("Could not signal existing instance: " + ex.Message); }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.Invoke(() => _mainWindow?.ShowFromTray()),
            null, -1, false);

        base.OnStartup(e);
        Mark("logger+mutex");

        Hardware = new HardwareInfoService();
        Settings = new SettingsService();
        Mark("SettingsService");
        Loc = new LocalizationService();
        Loc.Initialize(Settings.Current);
        Theme = new ThemeService();
        Theme.SetTheme(Settings.Current.ThemeColor);
        Power = new PowerPlanService(Settings);
        Awake = new PowerAwakeService(Settings);
        HardwareAccess = new DeferredHardwareAccess(() =>
            (IHardwareAccess?)HardwareServiceClient.TryStart() ?? new HardwareAccessCoordinator());
        Monitor = new MonitorService(HardwareAccess);
        Mark("MonitorService");
        Updates = new UpdateService(Settings);
        AutoStart = new StartupService();
        Automation = new AutomationEngine();
        HeavyApps = new HeavyAppDetectionService(Settings, Monitor.ReadGpu3DByProcess);
        FullscreenCoverage = new ProtectedFullscreenCoverageService(() =>
        {
            HeavyAppDetectionState state = HeavyApps.Current;
            if (!state.ProtectedWorkloadActive) return new HashSet<int>();
            return state.ProtectedProcesses.Select(process => process.ProcessId).ToHashSet();
        });
        AppProfiles = new AppPowerProfileService(Settings);
        PowerSourcePlans = new PowerSourcePlanService(Settings);
        ThermalGuard = new ThermalGuardService(Settings);
        IdlePowerGuard = new IdlePowerGuardService(Settings);
        PowerRequests = new PowerRequestCoordinator(
            Settings,
            Power,
            Awake,
            Automation,
            AppProfiles,
            HeavyApps,
            PowerSourcePlans,
            ThermalGuard,
            IdlePowerGuard,
            () => _adaptiveResourcesInitialized ? ResourcePressure.Current : null);
        PowerRequests.ActivePlanChanged += plan =>
        {
            ActivePlan = plan;
            ActivePlanChanged?.Invoke(plan);
        };
        PowerRequests.ManualOverrideChanged += state => ManualOverrideChanged?.Invoke(state);
        PowerRequests.CpuAutomationStateChanged += state =>
        {
            CpuAutomationState = state;
            CpuAutomationStateChanged?.Invoke(state);
        };
        PowerRequests.ActivePlanReasonChanged += state => ActivePlanReasonChanged?.Invoke(state);
        PowerRequests.PowerPlanConflictDetected += notification => PowerPlanConflictDetected?.Invoke(notification);
        UpdateCoordinator = new UpdateCoordinator(Updates, Settings, IsHeavyAppSessionActive);
        StandbyAutoCleaner = new StandbyAutoCleanerService(Settings,
            protectedWorkloadActive: () => IsHeavyAppSessionActive());
        _powerFlow = new PowerFlowService();
        BatteryHistory = new BatteryHistoryService();
        var widgetRuntime = new WidgetRuntimeContext(
            Hardware,
            Power,
            Settings,
            Updates,
            AutoStart,
            Monitor,
            Theme,
            Loc,
            Awake,
            FullscreenCoverage,
            PowerRequests,
            () => _adaptiveResourcesInitialized ? ResourcePressure.Current : new Performance.ResourcePressureState(),
            requestFresh => RefreshHardwareSamplingDemand(requestFresh),
            (webView, subscribeGlobalEvents) => new HostBridge(
                webView, Hardware, Power, Settings, Updates, AutoStart, Monitor, this, subscribeGlobalEvents));
        Widgets = new WidgetManager(
            Settings,
            Theme,
            Loc,
            () => WebViewEnvironment,
            requestFresh => RefreshHardwareSamplingDemand(requestFresh),
            (manager, item, environment, size, placement) =>
                new WidgetWindow(widgetRuntime, manager, item, environment, size, placement));
        ScheduledPowerActions = new ScheduledPowerActionService(Settings, new PowerActionExecutor(), new SystemClock());
        _remoteCommands = new RemoteCommandService();
        Services = new AppServiceGraph(
            Settings, Loc, Theme, Power, Awake, HardwareAccess, Monitor, Updates, AutoStart,
            Automation, HeavyApps, FullscreenCoverage, AppProfiles, PowerSourcePlans,
            ThermalGuard, IdlePowerGuard, StandbyAutoCleaner, _powerFlow, BatteryHistory,
            ScheduledPowerActions, _remoteCommands, PowerRequests, Widgets);
        _applicationLifecycle = CreateApplicationLifecycleCoordinator();
        _applicationLifecycle.Start();
        Mark("ApplicationLifecycle.Start");

        // Launched via jump list while closed: apply the command, stay in tray.
        bool startMinimized = e.Args.Contains("--minimized") || startupCommand != null;
        bool justUpdated    = e.Args.Contains("--updated");
        // Don't force env creation here: MainWindow/Widgets pull it lazily.
        _mainWindow = new MainWindow(this, startMinimized, justUpdated);
        Mark("MainWindow");
        if (!startMinimized) _mainWindow.Show();
        Mark("Show");
        // Widgets are best-effort: a broken widget must not abort startup.
        try { if (Settings.Current.Widgets.Enabled) Widgets.ShowEnabled(); }
        catch (Exception ex) { Logger.Error("Widget startup failed", ex); }

        if (startupCommand != null)
            _ = Task.Run(() => ApplyRemoteCommand(startupCommand));

        // Jump list is not needed in the first second; defer off the critical path.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(SetupJumpList));

        // Re-register logon task when schema is stale (e.g. Priority 7 → 5).
        MaybeMigrateAutostartTask();

        string timing = string.Join(", ", marks.Select(m => $"{m.Name}={m.Ms}ms"));
        Logger.Info("Startup complete. Timing: " + timing);
    }

    private void MaybeMigrateAutostartTask()
    {
        // Cheap gate first: after the one-time migration this costs an int compare.
        if (Settings.Current.AutostartTaskSchemaVersion >= StartupService.CurrentTaskSchemaVersion)
            return;

        // Everything below spawns schtasks.exe (IsEnabled included) — never on the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                // Autostart off: nothing to migrate, and a task registered later is already
                // built from the current schema — stamp anyway, or this retries every launch.
                if (AutoStart.IsEnabled() && !AutoStart.SetStartWithWindows(true)) return;
                Settings.Update(state =>
                    state.AutostartTaskSchemaVersion = StartupService.CurrentTaskSchemaVersion);
                Logger.Info("Autostart task schema now v" + StartupService.CurrentTaskSchemaVersion);
            }
            catch (Exception ex)
            {
                Logger.Warn("Autostart task migration failed: " + ex.Message);
            }
        });
    }

    /// <summary>Public entry for HostBridge to rebuild jump list after language change.</summary>
    public void SetupJumpListPublic() => SetupJumpList();

    private static Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            ValidationEnvironment.ApplicationDataRoot,
            "VoltManager", "WebView2");
        // Cap V8 + low-end tiles. SwiftShader keeps a GPU process but with a smaller
        // driver working set than the full hardware path on this dashboard.
        // --disable-gpu / --in-process-gpu either crashed or grew the renderer.
        // Do NOT set --renderer-process-limit: widgets are separate WebView hosts.
        //
        // --process-per-site is what makes those hosts affordable: the dashboard and
        // every widget are pages of the same site (https://app.local), so Chromium puts
        // them all in ONE renderer instead of spawning a full renderer per widget
        // window. Nothing about the pages changes — only how many processes host them.
        // The remaining switches turn off browser subsystems this app never uses
        // (component updater, phishing model, telemetry pings): all of them are pure
        // resident cost here because the WebView only ever loads local content.
        var opts = new CoreWebView2EnvironmentOptions(
            WebViewRuntimeOptions.BrowserArguments(ValidationEnvironment.RendererVariant));
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, opts);
    }

    private void HookGlobalExceptionHandlers()
    {
        // UI-thread unhandled exceptions are owned exclusively by App.Reliability
        // (fatal shutdown + crash diagnostic). Do not register a second
        // DispatcherUnhandledException handler that would MessageBox-and-continue.

        // Background-thread exceptions are fatal to the process; log before exit.
        // Crash diagnostic for domain unhandled is also captured by App.Reliability.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("Unhandled exception (terminating: " + args.IsTerminating + ")", ex);
            else
                Logger.Error("Unhandled non-CLR exception (terminating: " + args.IsTerminating + ")");
        };

        // Faulted Tasks whose exception was never observed: log and swallow.
        // These are not classified as fatal — they may be abandoned background work.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void SetupJumpList()
    {
        try
        {
            // Tasks point at the non-elevated helper so clicking them never
            // shows UAC; absent in dev builds, so the jump list is best-effort.
            string helper = Path.Combine(AppContext.BaseDirectory, "VoltManagerPlanSwitch.exe");
            if (!File.Exists(helper)) return;

            var jumpList = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            var loc = Loc;
            AddPlanTask(jumpList, helper, loc.T("JumpList_PlanSaver"), RemoteCommandProtocol.PowerSaverKey,
                loc.T("JumpList_PlanSaverDesc"));
            AddPlanTask(jumpList, helper, loc.T("JumpList_PlanBalanced"), RemoteCommandProtocol.BalancedKey,
                loc.T("JumpList_PlanBalancedDesc"));
            AddPlanTask(jumpList, helper, loc.T("JumpList_PlanPerformance"), RemoteCommandProtocol.PerformanceKey,
                loc.T("JumpList_PlanPerformanceDesc"));
            AddPlanTask(jumpList, helper, loc.T("JumpList_Automatic"), RemoteCommandProtocol.AutoKey,
                loc.T("JumpList_AutomaticDesc"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_KeepAwakeOn"), RemoteCommandProtocol.KeepAwakeOnKey,
                loc.T("JumpList_KeepAwakeOnDesc"), loc.T("JumpList_CategorySystem"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_KeepAwakeOff"), RemoteCommandProtocol.KeepAwakeOffKey,
                loc.T("JumpList_KeepAwakeOffDesc"), loc.T("JumpList_CategorySystem"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_Shutdown30"), RemoteCommandProtocol.Shutdown30Key,
                loc.T("JumpList_Shutdown30Desc"), loc.T("JumpList_CategorySchedule"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_Shutdown60"), RemoteCommandProtocol.Shutdown60Key,
                loc.T("JumpList_Shutdown60Desc"), loc.T("JumpList_CategorySchedule"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_Sleep30"), RemoteCommandProtocol.Sleep30Key,
                loc.T("JumpList_Sleep30Desc"), loc.T("JumpList_CategorySchedule"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_Sleep60"), RemoteCommandProtocol.Sleep60Key,
                loc.T("JumpList_Sleep60Desc"), loc.T("JumpList_CategorySchedule"));
            AddCommandTask(jumpList, helper, loc.T("JumpList_OpenScheduler"), RemoteCommandProtocol.OpenSchedulerKey,
                loc.T("JumpList_OpenSchedulerDesc"), loc.T("JumpList_CategorySchedule"));
            JumpList.SetJumpList(this, jumpList);
        }
        catch
        {
            // A broken jump list must not block startup.
        }
    }

    private void AddPlanTask(JumpList jumpList, string helper, string title, string key, string description)
        => AddJumpTask(jumpList, helper, title, RemoteCommandProtocol.PlanArgName + " " + key, description, Loc.T("JumpList_CategoryPlan"));

    private static void AddCommandTask(JumpList jumpList, string helper, string title, string key, string description, string category)
        => AddJumpTask(jumpList, helper, title, RemoteCommandProtocol.CommandArgName + " " + key, description, category);

    private static void AddJumpTask(JumpList jumpList, string helper, string title, string arguments, string description, string category)
    {
        jumpList.JumpItems.Add(new JumpTask
        {
            CustomCategory = category,
            Title = title,
            Description = description,
            ApplicationPath = helper,
            Arguments = arguments,
            WorkingDirectory = AppContext.BaseDirectory,
            IconResourcePath = helper,
            IconResourceIndex = 0,
        });
    }

    internal void ApplyRemoteCommand(string key)
    {
        try
        {
            switch (key)
            {
                case RemoteCommandProtocol.PowerSaverKey: SetManualOverride(PlanId.PowerSaver, null); break;
                case RemoteCommandProtocol.BalancedKey: SetManualOverride(PlanId.Balanced, null); break;
                case RemoteCommandProtocol.PerformanceKey: SetManualOverride(PlanId.Performance, null); break;
                case RemoteCommandProtocol.AutoKey: SetAutomaticMode(); break;
                case RemoteCommandProtocol.KeepAwakeOnKey: SetKeepAwake(true); break;
                case RemoteCommandProtocol.KeepAwakeOffKey: SetKeepAwake(false); break;
                case RemoteCommandProtocol.KeepAwakeToggleKey:
                    SetKeepAwake(!(Settings.Current.KeepAwake?.Enabled == true));
                    break;
                case RemoteCommandProtocol.Shutdown30Key:
                    ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Shutdown);
                    break;
                case RemoteCommandProtocol.Shutdown60Key:
                    ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(60), ScheduledPowerActionType.Shutdown);
                    break;
                case RemoteCommandProtocol.Sleep30Key:
                    ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(30), ScheduledPowerActionType.Sleep);
                    break;
                case RemoteCommandProtocol.Sleep60Key:
                    ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(60), ScheduledPowerActionType.Sleep);
                    break;
                case RemoteCommandProtocol.OpenSchedulerKey:
                    Dispatcher.Invoke(() =>
                    {
                        _mainWindow?.ShowFromTray();
                        // Navigate to system view after showing.
                        _mainWindow?.NavigateToSystemView();
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            // Remote commands must never crash the app.
            Logger.Error("Remote command failed: " + key, ex);
        }
    }

    private ApplicationLifecycleCoordinator CreateApplicationLifecycleCoordinator()
        => new(new ApplicationLifecycleActions(
            Attach: () =>
            {
                Monitor.MetricsUpdated += OnMetricsSampled;
                Settings.SettingsChanged += OnLifecycleSettingsChanged;
                SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
                if (_remoteCommands != null)
                    _remoteCommands.CommandReceived += ApplyRemoteCommand;
            },
            Detach: () =>
            {
                Monitor.MetricsUpdated -= OnMetricsSampled;
                Settings.SettingsChanged -= OnLifecycleSettingsChanged;
                SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
                if (_remoteCommands != null)
                    _remoteCommands.CommandReceived -= ApplyRemoteCommand;
            },
            StartServices: StartRuntimeServices,
            StopServices: StopRuntimeServices,
            CreatePlanPollTimer: CreatePlanPollTimer,
            CreateBatteryHistoryTimer: CreateBatteryHistoryTimer));

    private void OnLifecycleSettingsChanged(AppSettings _)
    {
        UpdateSamplingPeriod();
        RefreshHardwareSamplingDemand();
    }

    private void StartRuntimeServices()
    {
        PowerRequests.Start();
        Monitor.Start(PowerRequests.CurrentSamplingInterval);
        FullscreenCoverage.Start();
        HeavyApps.StartDelayed(TimeSpan.FromSeconds(2));
        AppProfiles.StartDelayed(TimeSpan.FromSeconds(3));
        StandbyAutoCleaner.StartDelayed(TimeSpan.FromSeconds(5));
        ScheduledPowerActions.Start();
        try { _remoteCommands?.Start(); }
        catch (Exception ex) { Logger.Error("Remote command listener failed to start", ex); }
    }

    private void StopRuntimeServices()
    {
        SafeCleanup("remote commands", () => _remoteCommands?.Stop());
        SafeCleanup("scheduled power action service", ScheduledPowerActions.Stop);
        SafeCleanup("standby cleaner", StandbyAutoCleaner.Stop);
        SafeCleanup("app profiles", AppProfiles.Stop);
        SafeCleanup("heavy apps", HeavyApps.Stop);
        SafeCleanup("fullscreen coverage", FullscreenCoverage.Stop);
        SafeCleanup("monitor", Monitor.Stop);
        SafeCleanup("power requests", () => PowerRequests.Stop());
    }

    private IDisposable CreatePlanPollTimer(CancellationToken epoch)
        => new System.Threading.Timer(_ =>
        {
            if (_applicationLifecycle?.IsCurrent(epoch) != true) return;
            try { PowerRequests.RefreshActivePlanFromSystem(DateTime.UtcNow); }
            catch (Exception ex) { Logger.Error("Plan poll failed", ex); }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

    private IDisposable CreateBatteryHistoryTimer(CancellationToken epoch)
        => new System.Threading.Timer(_ =>
        {
            if (_applicationLifecycle?.IsCurrent(epoch) != true) return;
            try
            {
                var state = _powerFlow.GetState();
                double? temp = Monitor.Latest.CpuTemp ?? Monitor.Latest.GpuTemp;
                if (_applicationLifecycle?.IsCurrent(epoch) == true)
                    BatteryHistory.Record(state, temp, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Logger.Error("Battery history sample failed", ex);
            }
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));

    private void OnMetricsSampled(MetricsSnapshot metrics)
        => PowerRequests.ProcessMetrics(metrics, DateTime.UtcNow);

    private void UpdateSamplingPeriod()
        => PowerRequests.UpdateSamplingPeriod(Monitor);

    public KeepAwakeState SetKeepAwake(bool enabled) => Awake.SetEnabled(enabled);

    public bool SetManualOverride(
        PlanId plan,
        TimeSpan? duration,
        string source = "manual",
        string reasonCode = "manual_override")
        => PowerRequests.SetManualOverride(plan, duration, source, reasonCode);

    /// <summary>Removes any manual override and re-enables automation ("Automatico").</summary>
    public void SetAutomaticMode() => PowerRequests.SetAutomaticMode();

    public void ClearManualOverride() => PowerRequests.ClearManualOverride();

    public HeavyAppDetectionState GetHeavyAppStatus()
        => PowerRequests.GetHeavyAppStatus();

    public HeavyAppDetectionState RefreshHeavyAppDetection()
        => PowerRequests.RefreshHeavyAppDetection();

    /// <summary>True while a protected game/workload session, including teardown cooldown, is active.</summary>
    public bool IsHeavyAppSessionActive()
        => PowerRequests.IsProtectedWorkloadActive();

    /// <summary>
    /// Queues an update install URL for after the current game session ends.
    /// Overwrites any previous deferred URL (latest available installer wins).
    /// </summary>
    public void DeferUpdateUntilGameEnds(string downloadUrl)
        => UpdateCoordinator.DeferInstall(downloadUrl);

    /// <summary>Returns and clears a previously deferred update URL, if any.</summary>
    public string? TakeDeferredUpdateUrl()
        => UpdateCoordinator.TakeDeferredInstall();

    public bool HasDeferredUpdate()
        => UpdateCoordinator.HasDeferredInstall;

    public AppPowerProfileState GetAppPowerProfileStatus()
        => PowerRequests.GetAppPowerProfileStatus();

    public AppPowerProfileState RefreshAppPowerProfiles()
        => PowerRequests.RefreshAppPowerProfiles();

    public ActivePlanReasonState GetActivePlanReason()
        => PowerRequests.GetActivePlanReason();

    public PowerSourcePlanState GetPowerSourcePlanState()
        => PowerRequests.GetPowerSourcePlanState();

    public PowerSourcePlanState SetPowerSourcePlanSwitch(bool enabled)
        => PowerRequests.SetPowerSourcePlanSwitch(enabled);

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try
        {
            HardwareAccess.Invalidate();
        }
        catch (Exception ex)
        {
            Logger.Warn("Hardware resume handling failed: " + ex.Message);
        }
    }

    public void ExitApp()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        SafeCleanup("main window runtime", () => _mainWindow?.StopRuntime());
        SafeCleanup("widgets", Widgets.Dispose);
        SafeCleanup("application lifecycle", () => _applicationLifecycle?.Dispose());
        SafeCleanup("application services", DisposeApplicationServices);
        SafeCleanup("show wait", () => _showWait?.Unregister(null));
        SafeCleanup("show event", () => _showEvent?.Dispose());
        SafeCleanup("mutex", ReleaseApplicationMutex);
        Shutdown();
    }

    private void DisposeApplicationServices()
    {
        if (Interlocked.Exchange(ref _serviceDisposalStarted, 1) != 0) return;
        SafeCleanup("power requests", PowerRequests.Dispose);
        SafeCleanup("update coordinator", UpdateCoordinator.Dispose);
        SafeCleanup("update service", Updates.Dispose);
        SafeCleanup("scheduled power action service", ScheduledPowerActions.Dispose);
        SafeCleanup("remote commands", () => _remoteCommands?.Dispose());
        SafeCleanup("standby cleaner", StandbyAutoCleaner.Dispose);
        SafeCleanup("app profiles", AppProfiles.Dispose);
        SafeCleanup("heavy apps", HeavyApps.Dispose);
        SafeCleanup("fullscreen coverage", FullscreenCoverage.Dispose);
        SafeCleanup("monitor", Monitor.Dispose);
        SafeCleanup("hardware access", HardwareAccess.Dispose);
        SafeCleanup("keep awake", Awake.Dispose);
    }

    private static void SafeCleanup(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Warn("Cleanup failed (" + what + "): " + ex.Message); }
    }
}
