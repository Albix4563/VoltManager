using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
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
    public StandbyAutoCleanerService StandbyAutoCleaner { get; private set; } = null!;
    public BatteryHistoryService BatteryHistory { get; private set; } = null!;
    public ThemeService Theme { get; private set; } = null!;
    public WidgetManager Widgets { get; private set; } = null!;
    public Task<CoreWebView2Environment> WebViewEnvironment { get; private set; } = null!;

    private PowerFlowService _powerFlow = null!;
    private int _automationTickRunning;
    private TimeSpan _currentSamplingInterval = TimeSpan.FromSeconds(1);
    private System.Threading.Timer? _scheduledPowerActionTimer;
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

    public PowerPlan? ActivePlan { get; private set; }
    public CpuAutomationState CpuAutomationState { get; private set; } = new();
    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;
    public event Action<CpuAutomationState>? CpuAutomationStateChanged;
    public event Action<PowerPlanConflictNotification>? PowerPlanConflictDetected;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

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
                MessageBox.Show(
                    "VoltManager non è riuscito ad avviarsi.\n\n" +
                    "Dettagli salvati nel log:\n" + (Logger.LogFilePath ?? "(non disponibile)") +
                    "\n\nErrore: " + ex.Message,
                    "VoltManager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* never let the dialog mask the original failure */ }
            Shutdown(1);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
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

        Hardware = new HardwareInfoService();
        Settings = new SettingsService();
        Theme = new ThemeService();
        Theme.SetPreference(Settings.Current.Theme);
        Power = new PowerPlanService(Settings);
        Awake = new PowerAwakeService(Settings);
        Monitor = new MonitorService(Hardware);
        Updates = new UpdateService(Settings);
        AutoStart = new StartupService();
        Automation = new AutomationEngine();
        Settings.SettingsChanged += _ => UpdateSamplingPeriod();
        HeavyApps = new HeavyAppDetectionService(Settings);
        AppProfiles = new AppPowerProfileService(Settings);
        PowerSourcePlans = new PowerSourcePlanService(Settings);
        StandbyAutoCleaner = new StandbyAutoCleanerService(Settings);
        _powerFlow = new PowerFlowService();
        BatteryHistory = new BatteryHistoryService();
        WebViewEnvironment = CreateWebViewEnvironmentAsync();
        Widgets = new WidgetManager(this, WebViewEnvironment);
        var startupNow = DateTime.UtcNow;
        ClearExpiredManualOverride(startupNow);
        _planGuard.RefreshManualOverride(Settings.Current.Override, startupNow);

        _currentSamplingInterval = CpuAutomationSampleInterval();
        Monitor.MetricsUpdated += OnMetricsSampled;
        Monitor.Start(_currentSamplingInterval);
        HeavyApps.Start();
        AppProfiles.Start();
        StandbyAutoCleaner.Start();
        StartPlanPoll();
        StartScheduledPowerActionLoop();
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
        _mainWindow = new MainWindow(this, startMinimized, justUpdated, WebViewEnvironment);
        if (!startMinimized) _mainWindow.Show();
        // Widgets are best-effort: a broken widget must not abort startup.
        try { if (Settings.Current.Widgets.Enabled) Widgets.ShowEnabled(); }
        catch (Exception ex) { Logger.Error("Widget startup failed", ex); }

        if (startupCommand != null)
            _ = Task.Run(() => ApplyRemoteCommand(startupCommand));

        SetupJumpList();

        Logger.Info("Startup complete.");
    }

    private static Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "WebView2");
        return CoreWebView2Environment.CreateAsync(null, userDataFolder);
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
                "Si è verificato un errore imprevisto. VoltManager resta attivo, ma l'ultima operazione potrebbe non essere riuscita.\n\n" +
                "Dettagli salvati nel log:\n" + (Logger.LogFilePath ?? "(non disponibile)") +
                "\n\nErrore: " + e.Exception.Message,
                "VoltManager", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            AddPlanTask(jumpList, helper, "Risparmio energia", RemoteCommandProtocol.PowerSaverKey,
                "Blocca il piano Risparmio energia");
            AddPlanTask(jumpList, helper, "Bilanciato", RemoteCommandProtocol.BalancedKey,
                "Blocca il piano Bilanciato");
            AddPlanTask(jumpList, helper, "Prestazioni", RemoteCommandProtocol.PerformanceKey,
                "Blocca il piano Prestazioni");
            AddPlanTask(jumpList, helper, "Automatico", RemoteCommandProtocol.AutoKey,
                "Lascia scegliere il piano a VoltManager");
            AddCommandTask(jumpList, helper, "Tieni PC attivo", RemoteCommandProtocol.KeepAwakeOnKey,
                "Impedisce la sospensione automatica finché VoltManager è attivo", "Sistema");
            AddCommandTask(jumpList, helper, "Riprendi sospensione", RemoteCommandProtocol.KeepAwakeOffKey,
                "Ripristina le regole di sospensione del piano energetico", "Sistema");
            JumpList.SetJumpList(this, jumpList);
        }
        catch
        {
            // A broken jump list must not block startup.
        }
    }

    private static void AddPlanTask(JumpList jumpList, string helper, string title, string key, string description)
        => AddJumpTask(jumpList, helper, title, RemoteCommandProtocol.PlanArgName + " " + key, description, "Piano energetico");

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

    private void StartScheduledPowerActionLoop()
    {
        _scheduledPowerActionTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var scheduled = Settings.Current.AutoShutdown;
                if (scheduled is not { Enabled: true }) return;
                if (!TryParseScheduledPowerTime(scheduled.Time, out var scheduledTime)) return;

                var now = DateTime.Now;
                if (now.Hour != scheduledTime.Hour || now.Minute != scheduledTime.Minute) return;

                string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.Equals(scheduled.LastTriggeredLocalDate, today, StringComparison.Ordinal)) return;

                scheduled.LastTriggeredLocalDate = today;
                Settings.Save();
                ExecuteScheduledPowerAction(scheduled.Action);
            }
            catch (Exception ex)
            {
                // Scheduled power actions must never crash the app.
                Logger.Error("Scheduled power action check failed", ex);
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    private static bool TryParseScheduledPowerTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static void ExecuteScheduledPowerAction(string? action)
    {
        switch (NormalizeScheduledPowerAction(action))
        {
            case "restart":
                StartShutdownCommand("/r /t 0");
                break;
            case "sleep":
                SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
                break;
            default:
                StartShutdownCommand("/s /t 0");
                break;
        }
    }

    private static string NormalizeScheduledPowerAction(string? action) => action switch
    {
        "restart" => "restart",
        "sleep" => "sleep",
        _ => "shutdown",
    };

    private static void StartShutdownCommand(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
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
        SafeCleanup("metrics handler", () => Monitor.MetricsUpdated -= OnMetricsSampled);
        SafeCleanup("scheduled action timer", () => _scheduledPowerActionTimer?.Dispose());
        SafeCleanup("plan poll timer", () => _planPollTimer?.Dispose());
        SafeCleanup("battery history timer", () => _batteryHistoryTimer?.Dispose());
        SafeCleanup("monitor", Monitor.Dispose);
        SafeCleanup("heavy apps", HeavyApps.Dispose);
        SafeCleanup("app profiles", AppProfiles.Dispose);
        SafeCleanup("keep awake", Awake.Dispose);
        SafeCleanup("standby cleaner", StandbyAutoCleaner.Dispose);
        SafeCleanup("theme", Theme.Dispose);
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
