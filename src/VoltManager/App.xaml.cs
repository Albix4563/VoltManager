using System.IO;
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
    public MonitorService Monitor { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public StartupService AutoStart { get; private set; } = null!;
    public AutomationEngine Automation { get; private set; } = null!;

    private System.Threading.Timer? _automationTimer;
    private System.Threading.Timer? _planPollTimer;
    private MainWindow? _mainWindow;

    public PowerPlan? ActivePlan { get; private set; }
    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? startupPlan = RemoteCommandProtocol.ParsePlanArg(e.Args);

        _mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            // Another instance running: forward the plan command if any,
            // otherwise signal it to show its window, then quit.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(startupPlan != null
                    ? RemoteCommandProtocol.EventName(startupPlan)
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
        Monitor = new MonitorService(Hardware);
        Updates = new UpdateService(Settings);
        AutoStart = new StartupService();
        Automation = new AutomationEngine();
        ClearExpiredManualOverride(DateTime.UtcNow);

        Monitor.Start();
        StartPlanPoll();
        StartAutomationLoop();

        _remoteCommands = new RemoteCommandService();
        _remoteCommands.CommandReceived += ApplyRemoteCommand;
        _remoteCommands.Start();

        // Launched via jump list while closed: apply the plan, stay in tray.
        bool startMinimized = e.Args.Contains("--minimized") || startupPlan != null;
        bool justUpdated    = e.Args.Contains("--updated");
        _mainWindow = new MainWindow(this, startMinimized, justUpdated);
        if (!startMinimized) _mainWindow.Show();

        if (startupPlan != null)
            _ = Task.Run(() => ApplyRemoteCommand(startupPlan));

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
            JumpList.SetJumpList(this, jumpList);
        }
        catch
        {
            // A broken jump list must not block startup.
        }
    }

    private static void AddPlanTask(JumpList jumpList, string helper, string title, string key, string description)
    {
        jumpList.JumpItems.Add(new JumpTask
        {
            CustomCategory = "Piano energetico",
            Title = title,
            Description = description,
            ApplicationPath = helper,
            Arguments = RemoteCommandProtocol.PlanArgName + " " + key,
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

    public bool SetManualOverride(PlanId plan, TimeSpan? duration)
    {
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
        _planPollTimer?.Dispose();
        Monitor.Dispose();
        _remoteCommands?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }
}
