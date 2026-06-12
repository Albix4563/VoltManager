using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Models;

namespace VoltManager;

public partial class MainWindow : Window
{
    private readonly App _app;
    private HostBridge? _bridge;
    private bool _exiting;
    private readonly bool _justUpdated;

    public MainWindow(App app, bool startMinimized, bool justUpdated = false)
    {
        _app = app;
        _justUpdated = justUpdated;
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosingToTray;
        // Fires from timer threads; tooltip lives on the UI thread.
        _app.ActivePlanChanged += p => Dispatcher.Invoke(() =>
            TrayIcon.ToolTipText = "VoltManager – " + PlanDisplayName(p));

        if (startMinimized)
        {
            // Window object exists for tray + WebView lifetime, but stays hidden.
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Show();
            Hide();
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoltManager", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Runtime WebView2 non trovato. Installa \"Microsoft Edge WebView2 Runtime\" e riavvia VoltManager.\n\nDettagli: " + ex.Message,
                "VoltManager", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            _app.ExitApp();
            return;
        }

        var core = WebView.CoreWebView2;
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("app.local", wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        _bridge = new HostBridge(WebView, _app.Hardware, _app.Power, _app.Settings, _app.Updates, _app.AutoStart, _app);
        _bridge.Attach();
        _bridge.ExitRequested += () => Dispatcher.Invoke(() => { _exiting = true; _app.ExitApp(); });
        _bridge.MinimizeToTrayRequested += () => Dispatcher.Invoke(HideToTray);

        _app.Monitor.MetricsUpdated += m => _bridge.PushEvent("metrics", m);
        _app.ActivePlanChanged += p => _bridge.PushEvent("activePlanChanged", new { plan = p?.PlanId, guid = p?.Guid, name = p?.Name });
        _app.Settings.SettingsChanged += s => _bridge.PushEvent("automationStateChanged", new { masterEnabled = s.MasterAutomationEnabled, @override = s.Override });
        _app.ManualOverrideChanged += o => _bridge.PushEvent("manualOverrideChanged", new { @override = o });

        bool startupCheckDone = false;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess || startupCheckDone) return;
            startupCheckDone = true;
            if (_justUpdated)
                _ = PushUpdatedToastAsync();
            else
                _ = CheckForUpdatesOnStartupAsync();
        };

        core.Navigate("https://app.local/index.html?v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private async Task PushUpdatedToastAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        string ver = _app.Updates.CurrentVersion;
        _bridge?.PushEvent("appUpdated", new { version = ver });
        // After toast, run regular update check in background.
        _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            // Small delay so JS event handlers are registered before the push.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var info = await _app.Updates.CheckForUpdatesAsync();
            if (info.UpdateAvailable && info.DownloadUrl != null)
                _bridge?.PushEvent("updateAvailable", info);
        }
        catch
        {
            // Offline or rate-limited: stay silent, manual check remains available.
        }
    }

    private void OnClosingToTray(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_app.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
        else
        {
            _exiting = true;
            _app.ExitApp();
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private static string PlanDisplayName(Models.PowerPlan? plan) => plan?.PlanId switch
    {
        PlanId.PowerSaver => "Risparmio energia",
        PlanId.Balanced => "Bilanciato",
        PlanId.Performance => "Prestazioni",
        _ => string.IsNullOrEmpty(plan?.Name) ? "Sconosciuto" : plan.Name,
    };

    private void TrayMenu_Opened(object sender, RoutedEventArgs e)
    {
        TrayActivePlanItem.Header = "Piano attivo: " + PlanDisplayName(_app.ActivePlan);
        TrayAutomationItem.IsChecked = _app.Settings.Current.MasterAutomationEnabled;
        TrayClearOverrideItem.Visibility = _app.Settings.Current.Override != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TrayPlanDuration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag }) return;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int hours)) return;

        PlanId plan = parts[0] switch
        {
            "powerSaver" => PlanId.PowerSaver,
            "performance" => PlanId.Performance,
            _ => PlanId.Balanced,
        };
        TimeSpan? duration = hours == 0 ? null : TimeSpan.FromHours(hours);
        // SetManualOverride shells out to powercfg; keep it off the UI thread.
        _ = Task.Run(() => _app.SetManualOverride(plan, duration));
    }

    private void TrayClearOverride_Click(object sender, RoutedEventArgs e)
        => _ = Task.Run(_app.ClearManualOverride);

    private void TrayAutomation_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.Current.MasterAutomationEnabled = TrayAutomationItem.IsChecked;
        _app.Settings.Save();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        _app.ExitApp();
    }
}
