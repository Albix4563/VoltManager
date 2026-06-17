using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
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

    private System.Threading.Timer? _automationTimer;
    private System.Threading.Timer? _scheduledPowerActionTimer;
    private System.Threading.Timer? _planPollTimer;
    private MainWindow? _mainWindow;
    private bool _heavyAppPlanSessionActive;
    private PlanId? _planBeforeHeavyAppSession;
    private DateTime _heavyAppLastActiveUtc;
    private bool _appProfilePlanSessionActive;
    private PlanId? _planBeforeAppProfileSession;
    private DateTime _appProfileLastActiveUtc;
    // Grace before tearing down a heavy-app session: absorbs transient scan misses so an
    // alt-tabbed/minimized game does not immediately revert the power plan.
    private static readonly TimeSpan HeavyAppTeardownGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AppProfileTeardownGrace = TimeSpan.FromSeconds(15);

    public PowerPlan? ActivePlan { get; private set; }
    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    protected override void OnStartup(StartupEventArgs e)
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
            catch { }
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
        Power = new PowerPlanService(Settings);
        Awake = new PowerAwakeService(Settings);
        Monitor = new MonitorService(Hardware);
        Updates = new UpdateService(Settings);
        AutoStart = new StartupService();
        Automation = new AutomationEngine();
        HeavyApps = new HeavyAppDetectionService(Settings);
        AppProfiles = new AppPowerProfileService(Settings);
        ClearExpiredManualOverride(DateTime.UtcNow);

        Monitor.Start();
        HeavyApps.Start();
        AppProfiles.Start();
        StartPlanPoll();
        StartAutomationLoop();
        StartScheduledPowerActionLoop();

        _remoteCommands = new RemoteCommandService();
        _remoteCommands.CommandReceived += ApplyRemoteCommand;
        _remoteCommands.Start();

        // Launched via jump list while closed: apply the command, stay in tray.
        bool startMinimized = e.Args.Contains("--minimized") || startupCommand != null;
        bool justUpdated    = e.Args.Contains("--updated");
        _mainWindow = new MainWindow(this, startMinimized, justUpdated);
        if (!startMinimized) _mainWindow.Show();

        if (startupCommand != null)
            _ = Task.Run(() => ApplyRemoteCommand(startupCommand));

        SetupJumpList();
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
        catch
        {
            // Remote commands must never crash the app.
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
                if (current?.Guid != ActivePlan?.Guid)
                {
                    ActivePlan = current;
                    ActivePlanChanged?.Invoke(current);
                }
            }
            catch { }
        }, null, 0, 3000);
    }

    private void StartAutomationLoop()
    {
        _automationTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                double avg = Automation.AddSample(Monitor.Latest.Cpu);
                var now = DateTime.UtcNow;
                ClearExpiredManualOverride(now);

                if (HandleAppPowerProfiles(now))
                    return;

                if (HandleHeavyAppDetection(now))
                    return;

                var target = Automation.Evaluate(avg, now, ActivePlan?.PlanId, Settings.Current);
                if (target != null && Power.SetActivePlan(target.Value))
                {
                    var current = Power.GetActivePlan();
                    ActivePlan = current;
                    ActivePlanChanged?.Invoke(current);
                }
            }
            catch
            {
                // Automation must never crash the app.
            }
        }, null, 3000, 1000);
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
            catch
            {
                // Scheduled power actions must never crash the app.
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
        Settings.Save();
        Automation.Reset();

        var current = Power.GetActivePlan();
        ActivePlan = current;
        ActivePlanChanged?.Invoke(current);
        ManualOverrideChanged?.Invoke(Settings.Current.Override);
        return true;
    }

    /// <summary>Removes any manual override and re-enables automation ("Automatico").</summary>
    public void SetAutomaticMode()
    {
        Settings.Current.Override = null;
        Settings.Current.MasterAutomationEnabled = true;
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
    }

    public void ClearManualOverride()
    {
        if (Settings.Current.Override == null) return;

        Settings.Current.Override = null;
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
    }

    public HeavyAppDetectionState GetHeavyAppStatus() => HeavyApps.Current;

    public HeavyAppDetectionState RefreshHeavyAppDetection() => HeavyApps.Refresh();

    public AppPowerProfileState GetAppPowerProfileStatus() => AppProfiles.Current;

    public AppPowerProfileState RefreshAppPowerProfiles() => AppProfiles.Refresh();

    private void ClearExpiredManualOverride(DateTime now)
    {
        if (Settings.Current.Override?.ExpiresAtUtc == null) return;
        if (Settings.Current.Override.ExpiresAtUtc > now) return;

        Settings.Current.Override = null;
        Settings.Save();
        Automation.Reset();
        ManualOverrideChanged?.Invoke(null);
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
        _automationTimer?.Dispose();
        _scheduledPowerActionTimer?.Dispose();
        _planPollTimer?.Dispose();
        Monitor.Dispose();
        HeavyApps.Dispose();
        AppProfiles.Dispose();
        Awake.Dispose();
        _remoteCommands?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }
}
