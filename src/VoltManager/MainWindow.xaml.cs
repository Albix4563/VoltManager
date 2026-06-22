using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Models;
using VoltManager.Services;
using Media = System.Windows.Media;

namespace VoltManager;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoUpdateInitialDelay = TimeSpan.FromMinutes(30);
    private readonly App _app;
    private readonly Task<CoreWebView2Environment> _webViewEnvironment;
    private HostBridge? _bridge;
    private bool _exiting;
    private readonly bool _justUpdated;
    private System.Threading.Timer? _autoUpdateTimer;
    private int _autoUpdateCheckRunning;
    private bool _updatePromptOpen;
    private readonly GamingModeReminderService _gamingReminder = new();
    private int _gamingReminderPromptRunning;

    public MainWindow(App app, bool startMinimized, bool justUpdated = false,
        Task<CoreWebView2Environment>? webViewEnvironment = null)
    {
        _app = app;
        _webViewEnvironment = webViewEnvironment ?? app.WebViewEnvironment;
        _justUpdated = justUpdated;
        InitializeComponent();
        ApplyHostTheme(_app.Theme.ResolvedTheme);
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosingToTray;
        Closed += (_, _) => _autoUpdateTimer?.Dispose();
        // Fires from timer threads; tooltip lives on the UI thread.
        _app.ActivePlanChanged += p => Dispatcher.Invoke(() =>
            TrayIcon.ToolTipText = "VoltManager – " + PlanDisplayName(p));
        _app.Settings.SettingsChanged += s => Dispatcher.Invoke(() =>
        {
            _app.Theme.SetPreference(s.Theme);
            ApplyHostTheme(_app.Theme.ResolvedTheme);
        });
        _app.Theme.ThemeChanged += t => Dispatcher.Invoke(() =>
        {
            ApplyHostTheme(t);
            _bridge?.PushEvent("themeChanged", new { resolvedTheme = t });
        });

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
            await WebView.EnsureCoreWebView2Async(await _webViewEnvironment);
        }
        catch (Exception ex)
        {
            Logger.Error("WebView2 initialization failed", ex);
            MessageBox.Show(
                "Runtime WebView2 non trovato. Installa \"Microsoft Edge WebView2 Runtime\" e riavvia VoltManager.\n\nDettagli: " + ex.Message,
                "VoltManager", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            _app.ExitApp();
            return;
        }

        try
        {
            var core = WebView.CoreWebView2;
            string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            _bridge = new HostBridge(WebView, _app.Hardware, _app.Power, _app.Settings, _app.Updates, _app.AutoStart, _app.Monitor, _app);
            _bridge.Attach();
            _bridge.ExitRequested += () => Dispatcher.Invoke(() => { _exiting = true; _app.ExitApp(); });
            _bridge.MinimizeToTrayRequested += () => Dispatcher.Invoke(HideToTray);
            _bridge.GamingModeRequested += SetGamingModeFromBridgeAsync;
            _bridge.GamingModeStateRequested += GetGamingModeState;

            _app.Monitor.MetricsUpdated += OnMetricsUpdated;
            _app.ActivePlanChanged += p => _bridge.PushEvent("activePlanChanged", new { plan = p?.PlanId, guid = p?.Guid, name = p?.Name });
            _app.Settings.SettingsChanged += s => _bridge.PushEvent("automationStateChanged", new { masterEnabled = s.MasterAutomationEnabled, @override = s.Override });
            _app.CpuAutomationStateChanged += s => _bridge.PushEvent("cpuAutomationStateChanged", s);
            _app.ManualOverrideChanged += o =>
            {
                _bridge.PushEvent("manualOverrideChanged", new { @override = o });
                if (!IsPerformanceOverride(o, DateTime.UtcNow))
                    _gamingReminder.Stop();
                PushGamingModeState();
            };
            _app.Awake.StateChanged += s => _bridge.PushEvent("keepAwakeChanged", s);
            _app.PowerSourcePlans.StateChanged += s => _bridge.PushEvent("powerSourcePlanChanged", s);
            _app.Widgets.StateChanged += s => _bridge.PushEvent("widgetsStateChanged", s);

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

            StartAutoUpdateLoop();
            core.Navigate("https://app.local/index.html?v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (Exception ex)
        {
            // WebView came up but wiring the UI failed: the dashboard is unusable,
            // so report it and exit cleanly rather than leaving a blank window.
            Logger.Error("WebView UI setup failed", ex);
            MessageBox.Show(
                "Impossibile inizializzare l'interfaccia di VoltManager.\n\nDettagli: " + ex.Message,
                "VoltManager", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            _app.ExitApp();
        }
    }

    private void OnMetricsUpdated(MetricsSnapshot metrics)
    {
        _bridge?.PushEvent("metrics", metrics);

        if (_gamingReminder.ObserveCpu(metrics.Cpu, DateTime.UtcNow) != GamingModeReminderDecision.Prompt)
            return;

        if (Interlocked.Exchange(ref _gamingReminderPromptRunning, 1) == 1)
            return;

        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                ShowGamingModeReminder(metrics.Cpu);
            }
            finally
            {
                Interlocked.Exchange(ref _gamingReminderPromptRunning, 0);
            }
        });
    }

    private void ShowGamingModeReminder(double currentCpu)
    {
        if (!_gamingReminder.Active)
            return;

        var currentOverride = _app.Settings.Current.Override;
        if (!IsPerformanceOverride(currentOverride, DateTime.UtcNow))
        {
            _gamingReminder.Stop();
            return;
        }

        var result = MessageBox.Show(
            $"Hai il piano gaming attivo, ma la CPU risulta a riposo ({currentCpu:0.0}%) da diversi minuti.\n\nVuoi disattivare il piano gaming e riprendere il piano energetico automatico?",
            "Piano gaming attivo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        _gamingReminder.Stop();
        _ = Task.Run(_app.SetAutomaticMode);
    }

    private static bool IsPerformanceOverride(ManualOverride? manualOverride, DateTime nowUtc)
        => manualOverride?.IsActive(nowUtc) == true &&
           string.Equals(manualOverride.Plan, "performance", StringComparison.OrdinalIgnoreCase);

    private bool IsGamingModeActive()
        => _gamingReminder.Active && IsPerformanceOverride(_app.Settings.Current.Override, DateTime.UtcNow);

    private object GetGamingModeState()
    {
        bool active = IsGamingModeActive();
        return new { active, plan = active ? "performance" : null, @override = _app.Settings.Current.Override };
    }

    private void PushGamingModeState()
        => _bridge?.PushEvent("gamingModeChanged", GetGamingModeState());

    private async Task<object?> SetGamingModeFromBridgeAsync(bool enabled)
    {
        bool success = enabled
            ? await EnableGamingModeAsync()
            : await DisableGamingModeAsync();

        return new { success, state = GetGamingModeState() };
    }

    private async Task<bool> EnableGamingModeAsync()
    {
        _gamingReminder.Start(DateTime.UtcNow);

        try
        {
            bool applied = await Task.Run(() => _app.SetManualOverride(PlanId.Performance, null));
            if (applied)
            {
                PushGamingModeState();
                return true;
            }
        }
        catch
        {
            // Fall through to the same recovery path used when powercfg returns failure.
        }

        _gamingReminder.Stop();
        PushGamingModeState();
        return false;
    }

    private async Task<bool> DisableGamingModeAsync()
    {
        _gamingReminder.Stop();
        try
        {
            await Task.Run(_app.SetAutomaticMode);
            PushGamingModeState();
            return true;
        }
        catch
        {
            PushGamingModeState();
            return false;
        }
    }

    private void ApplyHostTheme(string? theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        var mediaColor = light ? Media.Color.FromRgb(246, 249, 252) : Media.Color.FromRgb(10, 17, 40);
        Background = new Media.SolidColorBrush(mediaColor);
        WebView.DefaultBackgroundColor = light
            ? Drawing.Color.FromArgb(246, 249, 252)
            : Drawing.Color.FromArgb(10, 17, 40);
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
            var autoUpdates = _app.Settings.Current.AutoUpdates;
            if (autoUpdates is not { Enabled: true }) return;

            var info = await _app.Updates.CheckForUpdatesAsync();
            if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl)) return;
            if (IsUpdateSuppressed(info, respectSnooze: true)) return;

            if (ShouldInstallUpdatesSilently())
                await DownloadAndInstallUpdateAsync(info.DownloadUrl);
            else
                _bridge?.PushEvent("updateAvailable", info);
        }
        catch (Exception ex)
        {
            // Offline or rate-limited: stay silent, manual check remains available.
            Logger.Warn("Startup update check failed: " + ex.Message);
        }
    }

    private void StartAutoUpdateLoop()
    {
        var interval = GetAutoUpdateInterval();
        _autoUpdateTimer = new System.Threading.Timer(_ =>
        {
            _ = Dispatcher.InvokeAsync(async () => await RunAutoUpdateCheckAsync());
        }, null, AutoUpdateInitialDelay, interval);
    }

    private TimeSpan GetAutoUpdateInterval()
    {
        int minutes = _app.Settings.Current.AutoUpdates?.IntervalMinutes ?? 30;
        if (minutes < 5) minutes = 30;
        if (minutes > 1440) minutes = 1440;
        return TimeSpan.FromMinutes(minutes);
    }

    private async Task RunAutoUpdateCheckAsync()
    {
        if (Interlocked.Exchange(ref _autoUpdateCheckRunning, 1) == 1) return;

        try
        {
            var autoUpdates = _app.Settings.Current.AutoUpdates;
            if (autoUpdates is not { Enabled: true }) return;
            if (autoUpdates.SnoozedUntilUtc is DateTime snoozedUntil && snoozedUntil > DateTime.UtcNow) return;

            var info = await _app.Updates.CheckForUpdatesAsync();
            if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl)) return;
            if (IsUpdateSuppressed(info, respectSnooze: true)) return;

            if (ShouldInstallUpdatesSilently())
                await DownloadAndInstallUpdateAsync(info.DownloadUrl);
            else if (IsAppInForeground())
                _bridge?.PushEvent("updateAvailable", info);
            else
                await ShowBackgroundUpdatePromptAsync(info);
        }
        catch (Exception ex)
        {
            // Automatic checks must stay silent when the network or GitHub is unavailable.
            Logger.Warn("Automatic update check failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _autoUpdateCheckRunning, 0);
        }
    }

    private bool IsUpdateSuppressed(UpdateInfo info, bool respectSnooze)
    {
        var autoUpdates = _app.Settings.Current.AutoUpdates;
        if (autoUpdates == null) return false;

        if (respectSnooze && autoUpdates.SnoozedUntilUtc is DateTime snoozedUntil && snoozedUntil > DateTime.UtcNow)
            return true;

        string latest = NormalizeVersion(info.LatestVersion);
        string skipped = NormalizeVersion(autoUpdates.SkippedVersion);
        return latest.Length > 0 && skipped.Length > 0 &&
               string.Equals(latest, skipped, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldInstallUpdatesSilently()
        => _app.Settings.Current.AutoUpdates is { Enabled: true, SilentInstallEnabled: true };

    private static string NormalizeVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? "" : version.Trim().TrimStart('v', 'V');

    private bool IsAppInForeground()
        => IsVisible && WindowState != WindowState.Minimized && IsActive;

    private async Task ShowBackgroundUpdatePromptAsync(UpdateInfo info)
    {
        if (_updatePromptOpen) return;
        _updatePromptOpen = true;
        try
        {
            var prompt = new UpdatePromptWindow(info, _app.Theme.ResolvedTheme);
            if (IsVisible) prompt.Owner = this;
            prompt.Icon = Icon;
            prompt.ShowDialog();

            switch (prompt.Action)
            {
                case UpdatePromptAction.Install:
                    await DownloadAndInstallUpdateAsync(info.DownloadUrl!);
                    break;
                case UpdatePromptAction.Snooze:
                    SnoozeUpdate(prompt.SnoozeMinutes);
                    break;
                case UpdatePromptAction.Skip:
                    SkipUpdateVersion(info.LatestVersion);
                    break;
            }
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(string url)
    {
        try
        {
            string path = await _app.Updates.DownloadUpdateAsync(url);
            Process.Start(new ProcessStartInfo(path,
                $"/update --pid {Environment.ProcessId}") { UseShellExecute = true });
            _exiting = true;
            _app.ExitApp();
        }
        catch (Exception ex)
        {
            Logger.Error("Update download/install failed", ex);
            MessageBox.Show("Download aggiornamento fallito: " + ex.Message,
                "VoltManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SnoozeUpdate(int minutes)
    {
        minutes = Math.Clamp(minutes, 5, 1440);
        _app.Settings.Current.AutoUpdates ??= new AutoUpdateSettings();
        _app.Settings.Current.AutoUpdates.SnoozedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        _app.Settings.Save();
    }

    private void SkipUpdateVersion(string? version)
    {
        string normalized = NormalizeVersion(version);
        if (normalized.Length == 0) return;
        _app.Settings.Current.AutoUpdates ??= new AutoUpdateSettings();
        _app.Settings.Current.AutoUpdates.SkippedVersion = normalized;
        _app.Settings.Current.AutoUpdates.SnoozedUntilUtc = null;
        _app.Settings.Save();
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
        TrayGamingPlanItem.IsChecked = IsGamingModeActive();
        TrayKeepAwakeItem.IsChecked = _app.Awake.GetState().Enabled;
        TrayAutomationItem.IsChecked = _app.Settings.Current.MasterAutomationEnabled;
        TrayClearOverrideItem.Visibility = _app.Settings.Current.Override != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void TrayGamingPlan_Click(object sender, RoutedEventArgs e)
    {
        bool enable = TrayGamingPlanItem.IsChecked;
        bool applied = enable
            ? await EnableGamingModeAsync()
            : await DisableGamingModeAsync();

        TrayGamingPlanItem.IsChecked = IsGamingModeActive();
        if (applied || !enable) return;

        MessageBox.Show(
            "Non sono riuscito ad attivare il piano gaming. Verifica che il piano Prestazioni sia disponibile e riprova.",
            "Piano gaming",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void TrayPlanDuration_Click(object sender, RoutedEventArgs e)
    {
        _gamingReminder.Stop();
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

    private void TrayKeepAwake_Click(object sender, RoutedEventArgs e)
    {
        bool enable = TrayKeepAwakeItem.IsChecked;
        _ = Task.Run(() => _app.SetKeepAwake(enable));
    }

    private void TrayClearOverride_Click(object sender, RoutedEventArgs e)
    {
        _gamingReminder.Stop();
        _ = Task.Run(_app.ClearManualOverride);
    }

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
