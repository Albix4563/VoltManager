using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager;

public partial class App : Application
{
    private const string MutexName = "VoltManager_SingleInstance_Mutex";
    private const string ShowEventName = "VoltManager_ShowWindow_Event";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private RemoteCommandService? _remoteCommands;

    public HardwareInfoService Hardware { get; private set; } = null!;
    public SettingsService Settings { get; private set; } = null!;
    public PowerPlanService Power { get; private set; } = null!;
    public PowerAwakeService Awake { get; private set; } = null!;
    public MonitorService Monitor { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public StartupService AutoStart { get; private set; } = null!;
    public AutomationEngine Automation { get; private set; } = null!;
    public HeavyAppDetectionService HeavyApps { get; private set; } = null!;
    public AppPowerProfileService AppProfiles { get; private set; } = null!;
    public PowerSourcePlanService PowerSourcePlans { get; private set; } = null!;
    public ThermalGuardService ThermalGuard { get; private set; } = null!;
    public IdlePowerGuardService IdlePowerGuard { get; private set; } = null!;
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
    private int _automationTickRunning;
    private TimeSpan _currentSamplingInterval = TimeSpan.FromSeconds(1);
    private System.Threading.Timer? _planPollTimer;
    private System.Threading.Timer? _batteryHistoryTimer;
    private MainWindow? _mainWindow;
    private bool _heavyAppPlanSessionActive;
    private PlanId? _planBeforeHeavyAppSession;
    private DateTime _heavyAppLastActiveUtc;
    private bool _appProfilePlanSessionActive;
    private PlanId? _planBeforeAppProfileSession;
    private DateTime _appProfileLastActiveUtc;
    private readonly PowerPlanGuardService _planGuard = new();
    // Grace before tearing down a heavy-app session: absorbs transient scan misses so an
    // alt-tabbed/minimized game does not immediately revert the power plan.
    private static readonly TimeSpan HeavyAppTeardownGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AppProfileTeardownGrace = TimeSpan.FromSeconds(15);

    // Update install deferred while a detected game/heavy app is running.
    private readonly object _deferredUpdateLock = new();
    private string? _deferredUpdateUrl;

    public PowerPlan? ActivePlan { get; private set; }
    public CpuAutomationState CpuAutomationState { get; private set; } = new();
    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;
    public event Action<CpuAutomationState>? CpuAutomationStateChanged;
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
            Shutdown(1);
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
        Monitor = new MonitorService();
        Mark("MonitorService");
        Updates = new UpdateService(Settings);
        AutoStart = new StartupService();
        Automation = new AutomationEngine();
        Settings.SettingsChanged += _ => UpdateSamplingPeriod();
        HeavyApps = new HeavyAppDetectionService(Settings);
        AppProfiles = new AppPowerProfileService(Settings);
        PowerSourcePlans = new PowerSourcePlanService(Settings);
        ThermalGuard = new ThermalGuardService(Settings);
        IdlePowerGuard = new IdlePowerGuardService(Settings);
        StandbyAutoCleaner = new StandbyAutoCleanerService(Settings);
        _powerFlow = new PowerFlowService();
        BatteryHistory = new BatteryHistoryService();
        Widgets = new WidgetManager(this, () => WebViewEnvironment);
        var startupNow = DateTime.UtcNow;
        ClearExpiredManualOverride(startupNow);
        _planGuard.RefreshManualOverride(Settings.Current.Override, startupNow);

        _currentSamplingInterval = CpuAutomationSampleInterval();
        Monitor.MetricsUpdated += OnMetricsSampled;
        Monitor.Start(_currentSamplingInterval);
        Mark("Monitor.Start");
        // Delay heavy process scans to avoid blocking startup: the first scan
        // enumerates every running process and opens multiple WMI/proc handles.
        // A 2 second staggered delay leaves the UI responsive before the first tick.
        HeavyApps.StartDelayed(TimeSpan.FromSeconds(2));
        AppProfiles.StartDelayed(TimeSpan.FromSeconds(3));
        StandbyAutoCleaner.StartDelayed(TimeSpan.FromSeconds(5));
        // Plan poll starts with a 1 s delay so the initial WMI call doesn't race
        // the monitor's first tick (both query Win32_Processor via WMI).
        StartPlanPollDelayed(TimeSpan.FromSeconds(1));
        ScheduledPowerActions = new ScheduledPowerActionService(Settings, new PowerActionExecutor(), new SystemClock());
        ScheduledPowerActions.Start();
        StartBatteryHistoryLoop();

        _remoteCommands = new RemoteCommandService();
        _remoteCommands.CommandReceived += ApplyRemoteCommand;
        // Jump-list remote command channel is best-effort: failing to register
        // listeners must not block the rest of startup.
        try { _remoteCommands.Start(); }
        catch (Exception ex) { Logger.Error("Remote command listener failed to start", ex); }

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
                Settings.Current.AutostartTaskSchemaVersion = StartupService.CurrentTaskSchemaVersion;
                Settings.Save();
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
            "--js-flags=--max-old-space-size=128 " +
            "--enable-low-end-device-mode " +
            "--process-per-site " +
            "--use-angle=swiftshader " +
            "--use-gl=angle " +
            "--force-gpu-mem-available-mb=32 " +
            "--disable-accelerated-2d-canvas " +
            "--disable-accelerated-video-decode " +
            "--disable-gpu-shader-disk-cache " +
            "--disk-cache-size=67108864 " +
            "--disable-background-networking " +
            "--disable-component-update " +
            "--disable-client-side-phishing-detection " +
            "--disable-breakpad " +
            "--no-pings " +
            "--disable-features=BackForwardCache,InterestFeedContentSuggestions,Translate," +
            "MediaRouter,OptimizationHints,AutofillServerCommunication");
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, opts);
    }

    private void HookGlobalExceptionHandlers()
    {
        // UI-thread exceptions: log, tell the user, and keep the app alive — a
        // single broken handler must not kill the tray app the user relies on.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Background-thread exceptions are fatal to the process; log before exit.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("Unhandled exception (terminating: " + args.IsTerminating + ")", ex);
            else
                Logger.Error("Unhandled non-CLR exception (terminating: " + args.IsTerminating + ")");
        };

        // Faulted Tasks whose exception was never observed: log and swallow.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI-thread exception", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                Loc.T("Dialog_UnexpectedError",
                    Logger.LogFilePath ?? Loc.T("UpdatePrompt_ND"),
                    e.Exception.Message),
                Loc.T("Dialog_VoltManagerTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Showing the dialog must not itself crash the handler.
        }
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

    private void ApplyRemoteCommand(string key)
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

    private void StartPlanPoll()
    {
        // Catches external switches (control panel, automation) too; bridge relays to UI.
        _planPollTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var current = Power.GetActivePlan();
                current = ReassertExpectedPlanIfNeeded(current, DateTime.UtcNow) ?? current;
                if (current?.Guid != ActivePlan?.Guid)
                {
                    ActivePlan = current;
                    ActivePlanChanged?.Invoke(current);
                }
            }
            catch (Exception ex) { Logger.Error("Plan poll failed", ex); }
        }, null, 0, 3000);
    }

    private void StartPlanPollDelayed(TimeSpan delay)
    {
        // Like StartPlanPoll but delays the first WMI query so it doesn't
        // compete with the monitor's first tick and other startup work.
        _planPollTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var current = Power.GetActivePlan();
                current = ReassertExpectedPlanIfNeeded(current, DateTime.UtcNow) ?? current;
                if (current?.Guid != ActivePlan?.Guid)
                {
                    ActivePlan = current;
                    ActivePlanChanged?.Invoke(current);
                }
            }
            catch (Exception ex) { Logger.Error("Plan poll failed", ex); }
        }, null, delay, TimeSpan.FromMilliseconds(3000));
    }

    private void OnMetricsSampled(MetricsSnapshot metrics)
    {
        if (Interlocked.Exchange(ref _automationTickRunning, 1) == 1)
            return;

        try
        {
            var now = DateTime.UtcNow;
            double avg = Automation.AddSample(metrics.Cpu, now);
            ClearExpiredManualOverride(now);
            _planGuard.RefreshManualOverride(Settings.Current.Override, now);

            bool handledByHigherPriority =
                HandlePowerSourcePlans(now) ||
                HandleThermalGuard(now, metrics) ||
                HandleIdlePowerGuard(now) ||
                HandleAppPowerProfiles(now) ||
                HandleHeavyAppDetection(now);

            if (!handledByHigherPriority)
            {
                var target = Automation.Evaluate(avg, now, ActivePlan?.PlanId, Settings.Current);
                if (target != null && Power.SetActivePlan(target.Value))
                {
                    var current = Power.GetActivePlan();
                    ActivePlan = current;
                    ActivePlanChanged?.Invoke(current);
                }
            }

            PublishCpuAutomationState(now);
        }
        catch (Exception ex)
        {
            // Automation must never crash the shared metrics/automation sample.
            Logger.Error("Automation sample handling failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _automationTickRunning, 0);
        }
    }

    private TimeSpan CpuAutomationSampleInterval()
    {
        Settings.Current.CpuAutomation ??= new CpuAutomationSettings();
        Settings.Current.CpuAutomation.Normalize();
        return TimeSpan.FromSeconds(Settings.Current.CpuAutomation.SampleIntervalSeconds);
    }

    private void UpdateSamplingPeriod()
    {
        var interval = CpuAutomationSampleInterval();
        if (interval == _currentSamplingInterval)
        {
            PublishCpuAutomationState(DateTime.UtcNow);
            return;
        }

        _currentSamplingInterval = interval;
        Monitor.SetInterval(interval);
        Automation.Reset();
        PublishCpuAutomationState(DateTime.UtcNow);
    }

    private void PublishCpuAutomationState(DateTime now)
    {
        Settings.Current.CpuAutomation ??= new CpuAutomationSettings();
        Settings.Current.CpuAutomation.Normalize();
        bool manualOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var candidate = string.IsNullOrWhiteSpace(Automation.CandidateRuleId)
            ? null
            : Settings.Current.Rules.FirstOrDefault(r => r.Id == Automation.CandidateRuleId);

        CpuAutomationState = new CpuAutomationState
        {
            Enabled = Settings.Current.MasterAutomationEnabled && !manualOverrideActive,
            SampleIntervalSeconds = Settings.Current.CpuAutomation.SampleIntervalSeconds,
            RawCpu = Automation.LastRawCpu,
            AverageCpu = Automation.LastAverageCpu,
            SampledAtUtc = Automation.LastSampledAtUtc,
            CandidateRuleId = Automation.CandidateRuleId,
            CandidateTargetPlan = candidate?.TargetPlan,
            ActivePlan = ActivePlan?.PlanId,
            ManualOverrideActive = manualOverrideActive,
        };
        CpuAutomationStateChanged?.Invoke(CpuAutomationState);
    }

    private bool HandleAppPowerProfiles(DateTime now)
    {
        var config = Settings.Current.AppPowerProfiles ?? new AppPowerProfileSettings();
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        bool canAutoSwitch = Settings.Current.MasterAutomationEnabled && config.Enabled && !userOverrideActive;
        var state = AppProfiles.Current;

        if (canAutoSwitch && state.Active && state.TargetPlan != null)
        {
            _appProfileLastActiveUtc = now;
            if (!_appProfilePlanSessionActive)
            {
                _planBeforeAppProfileSession = ActivePlan?.PlanId;
                _appProfilePlanSessionActive = true;
                Automation.Reset();
            }

            var target = state.TargetPlan.Value;
            _planGuard.SetExpected(target, "appProfile", state.ActiveProfiles.FirstOrDefault()?.Name ?? "");
            if (ActivePlan?.PlanId == target)
                return true;

            if (Power.SetActivePlan(target))
            {
                var current = Power.GetActivePlan();
                ActivePlan = current;
                ActivePlanChanged?.Invoke(current);
            }
            return true;
        }

        if (_appProfilePlanSessionActive)
        {
            if (canAutoSwitch && now - _appProfileLastActiveUtc < AppProfileTeardownGrace)
                return true;

            _appProfilePlanSessionActive = false;
            var previous = _planBeforeAppProfileSession;
            _planBeforeAppProfileSession = null;
            _planGuard.ClearExpected("appProfile");
            Automation.Reset();

            if (!userOverrideActive && previous != null && ActivePlan?.PlanId != previous && Power.SetActivePlan(previous.Value))
            {
                var current = Power.GetActivePlan();
                ActivePlan = current;
                ActivePlanChanged?.Invoke(current);
            }
            return true;
        }

        return false;
    }

    private bool HandleHeavyAppDetection(DateTime now)
    {
        var config = Settings.Current.HeavyAppDetection;
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        bool canAutoSwitch = Settings.Current.MasterAutomationEnabled && config.Enabled && !userOverrideActive;
        var state = HeavyApps.Current;

        if (canAutoSwitch && state.Active)
        {
            _heavyAppLastActiveUtc = now;
            if (!_heavyAppPlanSessionActive)
            {
                _planBeforeHeavyAppSession = ActivePlan?.PlanId;
                _heavyAppPlanSessionActive = true;
                Automation.Reset();
            }

            var target = state.TargetPlan;
            _planGuard.SetExpected(target, "heavyApp", state.ActiveProcesses.FirstOrDefault()?.Name ?? "");
            if (ActivePlan?.PlanId == target)
                return true;

            if (Power.SetActivePlan(target))
            {
                var current = Power.GetActivePlan();
                ActivePlan = current;
                ActivePlanChanged?.Invoke(current);
            }
            return true;
        }

        if (_heavyAppPlanSessionActive)
        {
            // Keep the session (and performance plan) alive until the game has been gone for the
            // full grace window; a single missed scan or alt-tab must not revert the plan.
            if (canAutoSwitch && now - _heavyAppLastActiveUtc < HeavyAppTeardownGrace)
                return true;

            _heavyAppPlanSessionActive = false;
            var previous = _planBeforeHeavyAppSession;
            _planBeforeHeavyAppSession = null;
            _planGuard.ClearExpected("heavyApp");
            Automation.Reset();

            if (!userOverrideActive && previous != null && ActivePlan?.PlanId != previous && Power.SetActivePlan(previous.Value))
            {
                var current = Power.GetActivePlan();
                ActivePlan = current;
                ActivePlanChanged?.Invoke(current);
            }
            return true;
        }

        return false;
    }

    private bool HandleThermalGuard(DateTime now, MetricsSnapshot metrics)
    {
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = ThermalGuard.Evaluate(
            metrics.CpuTemp,
            metrics.GpuTemp,
            ActivePlan?.PlanId,
            userOverrideActive,
            Settings.Current.MasterAutomationEnabled,
            now);

        if (decision.State.Active)
            _planGuard.SetExpected(decision.State.TargetPlan, "thermal", decision.State.Message);
        else
            _planGuard.ClearExpected("thermal");

        if (decision.TargetPlan != null && Power.SetActivePlan(decision.TargetPlan.Value))
        {
            var current = Power.GetActivePlan();
            ActivePlan = current;
            ActivePlanChanged?.Invoke(current);
        }

        if (decision.BlocksLowerPriority)
            Automation.Reset();

        return decision.BlocksLowerPriority;
    }

    private bool HandleIdlePowerGuard(DateTime now)
    {
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = IdlePowerGuard.Evaluate(
            ActivePlan?.PlanId,
            userOverrideActive,
            Settings.Current.MasterAutomationEnabled,
            now);

        if (decision.State.Active)
            _planGuard.SetExpected(decision.State.TargetPlan, "idle", decision.State.Message);
        else
            _planGuard.ClearExpected("idle");

        if (decision.TargetPlan != null && Power.SetActivePlan(decision.TargetPlan.Value))
        {
            var current = Power.GetActivePlan();
            ActivePlan = current;
            ActivePlanChanged?.Invoke(current);
        }

        if (decision.BlocksLowerPriority)
            Automation.Reset();

        return decision.BlocksLowerPriority;
    }

    private bool HandlePowerSourcePlans(DateTime now)
    {
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = PowerSourcePlans.Evaluate(ActivePlan?.PlanId, userOverrideActive);
        var expectedPowerSourcePlan = ExpectedPowerSourcePlan(decision);
        if (expectedPowerSourcePlan != null)
            _planGuard.SetExpected(expectedPowerSourcePlan.Value, "powerSource", decision.State.Message);
        else
            _planGuard.ClearExpected("powerSource");

        if (decision.TargetPlan != null && Power.SetActivePlan(decision.TargetPlan.Value))
        {
            var current = Power.GetActivePlan();
            ActivePlan = current;
            ActivePlanChanged?.Invoke(current);
        }

        if (decision.BlocksLowerPriority)
            Automation.Reset();

        return decision.BlocksLowerPriority;
    }

    private static PlanId? ExpectedPowerSourcePlan(PowerSourcePlanDecision decision)
    {
        if (!decision.BlocksLowerPriority)
            return null;

        if (decision.TargetPlan != null)
            return decision.TargetPlan.Value;

        if (decision.State.LowBatteryActive)
            return PlanId.PowerSaver;

        if (decision.State.Active && decision.State.PluggedIn)
            return decision.State.PluggedPlan;

        return null;
    }

    private PowerPlan? ReassertExpectedPlanIfNeeded(PowerPlan? current, DateTime now)
    {
        if (!_planGuard.ShouldReassert(current?.PlanId, now, out var conflict) || conflict == null)
            return current;

        var suspects = PowerPlanGuardService.FindLikelyInterferingProcesses();
        var enriched = PowerPlanGuardService.WithSuspectsAndMessage(conflict, suspects);
        Logger.Warn(enriched.Message);

        if (!Power.SetActivePlan(conflict.ExpectedPlan))
            return current;

        var restored = Power.GetActivePlan();
        if (enriched.ShouldNotifyUser)
            PowerPlanConflictDetected?.Invoke(enriched);
        return restored ?? current;
    }

    private void StartBatteryHistoryLoop()
    {
        // Campiona la batteria ~1/min anche con la finestra in tray, così la cronologia
        // riflette l'uso reale e non solo i momenti col dashboard aperto. Il servizio
        // applica il proprio throttle; su desktop senza batteria Record() è un no-op.
        _batteryHistoryTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var state = _powerFlow.GetState();
                double? temp = Monitor.Latest.CpuTemp ?? Monitor.Latest.GpuTemp;
                BatteryHistory.Record(state, temp, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Il campionamento storico non deve mai far crashare l'app.
                Logger.Error("Battery history sample failed", ex);
            }
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    public KeepAwakeState SetKeepAwake(bool enabled) => Awake.SetEnabled(enabled);

    public bool SetManualOverride(PlanId plan, TimeSpan? duration)
    {
        _appProfilePlanSessionActive = false;
        _planBeforeAppProfileSession = null;
        _heavyAppPlanSessionActive = false;
        _planBeforeHeavyAppSession = null;

        if (!Power.SetActivePlan(plan)) return false;

        Settings.Current.Override = new ManualOverride
        {
            Plan = ToPlanKey(plan),
            ExpiresAtUtc = duration == null ? null : DateTime.UtcNow.Add(duration.Value),
        };
        _planGuard.SetExpected(plan, "manualOverride", ToPlanKey(plan));
        Settings.Save();
        Automation.Reset();

        var current = Power.GetActivePlan();
        ActivePlan = current;
        ActivePlanChanged?.Invoke(current);
        ManualOverrideChanged?.Invoke(Settings.Current.Override);
        PublishCpuAutomationState(DateTime.UtcNow);
        return true;
    }

    /// <summary>Removes any manual override and re-enables automation ("Automatico").</summary>
    public void SetAutomaticMode()
    {
        Settings.Current.Override = null;
        Settings.Current.MasterAutomationEnabled = true;
        _planGuard.ClearExpected();
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(DateTime.UtcNow);
    }

    public void ClearManualOverride()
    {
        if (Settings.Current.Override == null) return;

        Settings.Current.Override = null;
        _planGuard.ClearExpected("manualOverride");
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(DateTime.UtcNow);
    }

    public HeavyAppDetectionState GetHeavyAppStatus() => HeavyApps.Current;

    public HeavyAppDetectionState RefreshHeavyAppDetection() => HeavyApps.Refresh();

    /// <summary>True when detection reports an active game / heavy app session.</summary>
    public bool IsHeavyAppSessionActive() => HeavyApps.Current.Active;

    /// <summary>
    /// Queues an update install URL for after the current game session ends.
    /// Overwrites any previous deferred URL (latest available installer wins).
    /// </summary>
    public void DeferUpdateUntilGameEnds(string downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return;
        lock (_deferredUpdateLock)
            _deferredUpdateUrl = downloadUrl.Trim();
        Logger.Info("Update install deferred until game/heavy app session ends.");
    }

    /// <summary>Returns and clears a previously deferred update URL, if any.</summary>
    public string? TakeDeferredUpdateUrl()
    {
        lock (_deferredUpdateLock)
        {
            var url = _deferredUpdateUrl;
            _deferredUpdateUrl = null;
            return url;
        }
    }

    public bool HasDeferredUpdate()
    {
        lock (_deferredUpdateLock)
            return !string.IsNullOrWhiteSpace(_deferredUpdateUrl);
    }

    public AppPowerProfileState GetAppPowerProfileStatus() => AppProfiles.Current;

    public AppPowerProfileState RefreshAppPowerProfiles() => AppProfiles.Refresh();

    public PowerSourcePlanState GetPowerSourcePlanState()
        => PowerSourcePlans.RefreshState(Settings.Current.Override?.IsActive(DateTime.UtcNow) == true);

    public PowerSourcePlanState SetPowerSourcePlanSwitch(bool enabled)
    {
        PowerSourcePlans.SetEnabled(enabled, Settings.Current.Override?.IsActive(DateTime.UtcNow) == true);

        HandlePowerSourcePlans(DateTime.UtcNow);
        return PowerSourcePlans.Current;
    }

    private void ClearExpiredManualOverride(DateTime now)
    {
        if (Settings.Current.Override?.ExpiresAtUtc == null) return;
        if (Settings.Current.Override.ExpiresAtUtc > now) return;

        Settings.Current.Override = null;
        _planGuard.ClearExpected("manualOverride");
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(now);
    }

    private static string ToPlanKey(PlanId plan) => plan switch
    {
        PlanId.PowerSaver => "powerSaver",
        PlanId.Balanced => "balanced",
        PlanId.Performance => "performance",
        _ => "",
    };

    public void ExitApp()
    {
        // Each step is independent: one failing teardown must not skip the
        // rest, and above all must not prevent Shutdown().
        SafeCleanup("scheduled power action service", ScheduledPowerActions.Dispose);
        SafeCleanup("metrics handler", () => Monitor.MetricsUpdated -= OnMetricsSampled);
        SafeCleanup("plan poll timer", () => _planPollTimer?.Dispose());
        SafeCleanup("battery history timer", () => _batteryHistoryTimer?.Dispose());
        SafeCleanup("monitor", Monitor.Dispose);
        SafeCleanup("heavy apps", HeavyApps.Dispose);
        SafeCleanup("app profiles", AppProfiles.Dispose);
        SafeCleanup("keep awake", Awake.Dispose);
        SafeCleanup("standby cleaner", StandbyAutoCleaner.Dispose);
        SafeCleanup("widgets", Widgets.Dispose);
        SafeCleanup("remote commands", () => _remoteCommands?.Dispose());
        SafeCleanup("show wait", () => _showWait?.Unregister(null));
        SafeCleanup("show event", () => _showEvent?.Dispose());
        SafeCleanup("mutex", () =>
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        });
        Shutdown();
    }

    private static void SafeCleanup(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Warn("Cleanup failed (" + what + "): " + ex.Message); }
    }
}
