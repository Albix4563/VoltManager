using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;

namespace VoltManager;

public partial class MainWindow : Window
{
    private readonly App _app;
    private HostBridge? _bridge;
    private bool _exiting;

    public MainWindow(App app, bool startMinimized)
    {
        _app = app;
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosingToTray;

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
            _ = CheckForUpdatesOnStartupAsync();
        };

        core.Navigate("https://app.local/index.html");
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

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        _app.ExitApp();
    }
}
