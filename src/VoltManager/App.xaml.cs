using System.Threading;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            // Another instance running: signal it to show its window, then quit.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
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

        Monitor.Start();
        StartPlanPoll();
        StartAutomationLoop();

        bool startMinimized = e.Args.Contains("--minimized");
        _mainWindow = new MainWindow(this, startMinimized);
        if (!startMinimized) _mainWindow.Show();
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
                var target = Automation.Evaluate(avg, DateTime.UtcNow, ActivePlan?.PlanId, Settings.Current);
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

    public void ExitApp()
    {
        _automationTimer?.Dispose();
        _planPollTimer?.Dispose();
        Monitor.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }
}
