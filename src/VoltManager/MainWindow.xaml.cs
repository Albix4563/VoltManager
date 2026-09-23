using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;
using Media = System.Windows.Media;

namespace VoltManager;

public partial class MainWindow : Window
{
    private readonly App _app;
    private Task<CoreWebView2Environment>? _webViewEnvironment;
    private HostBridge? _bridge;
    private bool _exiting;
    private readonly bool _justUpdated;
    private bool _updatePromptOpen;
    private readonly GamingModeReminderService _gamingReminder = new();
    private int _gamingReminderPromptRunning;
    private bool _hostEventsWired;
    private volatile bool _webViewVisible;
    private readonly bool _startMinimized;
    private bool _webViewReady;
    private int _webViewInitRunning;
    private bool _dashboardNavigationPending;
    private bool _dashboardLoadFailed;
    private readonly WebViewTrayCoordinator _webViewTray;
    private readonly WebViewLifecycleBinding<CoreWebView2> _webViewLifecycleBinding;
    private bool _startupToastDone;
    private int _runtimeStopped;
    // Stable document version for HTTP/V8 code cache across tray reopens (not wall-clock).
    private static readonly string AppDocumentVersion =
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly Stopwatch _navStopwatch = new();
    private readonly GlobalHotkeyService _globalHotkeys = new();
    private IReadOnlyDictionary<string, bool> _lastHotkeyRegistrations =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _hotkeySource;

    private static readonly TimeSpan SlowRestoreFeedbackDelay = TimeSpan.FromMilliseconds(150);

    public MainWindow(App app, bool startMinimized, bool justUpdated = false,
        Task<CoreWebView2Environment>? webViewEnvironment = null)
    {
        _app = app;
        // May stay null until EnsureWebViewAsync — tray-only sessions skip Chromium.
        _webViewEnvironment = webViewEnvironment;
        _justUpdated = justUpdated;
        _startMinimized = startMinimized;
        InitializeComponent();
        _webViewLifecycleBinding = new WebViewLifecycleBinding<CoreWebView2>(
            AttachWebViewLifecycle,
            DetachWebViewLifecycle);
        _webViewTray = new WebViewTrayCoordinator(new DashboardSurface(this));
        _webViewTray.Start(initiallyVisible: false);
        SourceInitialized += (_, _) => BindGlobalHotkeys();
        ApplyHostTheme(_app.Theme.CurrentTheme);
        // Tray-only launch: keep Chromium unborn until the user opens the window.
        Loaded += async (_, _) =>
        {
            if (!_startMinimized || IsVisible && WindowState != WindowState.Minimized)
                await ShowFromTrayWithFeedbackAsync();
        };
        IsVisibleChanged += (_, _) => UpdateWebViewVisibility();
        StateChanged += (_, _) => UpdateWebViewVisibility();
        Closing += OnClosingToTray;
        Closed += (_, _) => StopRuntime();
        // Fires from timer threads; tooltip lives on the UI thread.
        _app.ActivePlanChanged += p => Dispatcher.Invoke(() =>
            TrayIcon.ToolTipText = "VoltManager – " + PlanDisplayName(p));
        _app.Settings.SettingsChanged += s => Dispatcher.Invoke(() =>
        {
            _app.Theme.SetTheme(s.ThemeColor);
            // Keep the main WebView font in sync with disk (import/other writers).
            _bridge?.PushEvent(BridgeEventNames.FontChanged, new { font = s.Font });
            BindGlobalHotkeys();
        });
        _app.Theme.ThemeChanged += themeColor => Dispatcher.Invoke(() =>
        {
            ApplyHostTheme(themeColor);
            _bridge?.PushEvent(BridgeEventNames.ThemeChanged, _app.Theme.GetWebTheme());
        });
        _app.Loc.LanguageChanged += (code, culture) => Dispatcher.Invoke(() =>
        {
            LocalizeTrayMenu();
            _app.Widgets.PushLanguage();
            _bridge?.PushEvent(BridgeEventNames.LanguageChanged, new { language = code, locale = culture.Name });
        });
        _app.HeavyApps.ActivityChanged += OnHeavyAppActivityChangedForUpdates;
        _app.UpdateCoordinator.UpdateAvailable += OnCoordinatorUpdateAvailable;
        _app.UpdateCoordinator.InstallRequested += OnCoordinatorInstallRequested;
        LocalizeTrayMenu();
        InitializeAutoUpdateLifecycle();

        if (startMinimized)
        {
            // Window object exists for tray lifetime; WebView stays uncreated.
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Show();
            Hide();
        }
    }

    private void BindGlobalHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _hotkeySource ??= HwndSource.FromHwnd(hwnd);
        if (_hotkeySource != null && !_hotkeyHookInstalled)
        {
            _hotkeySource.AddHook(GlobalHotkeyWndProc);
            _hotkeyHookInstalled = true;
        }

        _lastHotkeyRegistrations = _globalHotkeys.Rebind(hwnd, _app.Settings.Current.GlobalHotkeys);
        _bridge?.PushEvent(BridgeEventNames.GlobalHotkeysChanged, new { registrations = _lastHotkeyRegistrations });
    }

    private bool _hotkeyHookInstalled;

    private IntPtr GlobalHotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != GlobalHotkeyService.WmHotkey || !_globalHotkeys.TryGetCommand(wParam.ToInt32(), out var command))
            return IntPtr.Zero;

        handled = true;
        _ = Task.Run(() => _app.ApplyRemoteCommand(command));
        return IntPtr.Zero;
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady && WebView.CoreWebView2 != null) return;
        if (Interlocked.Exchange(ref _webViewInitRunning, 1) == 1) return;
        try
        {
            await InitWebViewAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _webViewInitRunning, 0);
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _webViewEnvironment ??= _app.WebViewEnvironment;
            await WebView.EnsureCoreWebView2Async(await _webViewEnvironment);
        }
        catch (Exception ex)
        {
            Logger.Error("WebView2 initialization failed", ex);
            throw;
        }

        try
        {
            WireWebViewCore(firstBoot: !_hostEventsWired);
            _webViewReady = true;
        }
        catch (Exception ex)
        {
            Logger.Error("WebView UI setup failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Binds CoreWebView2 + HostBridge. App-level event subscriptions run once
    /// (firstBoot); recovery after BrowserProcessExited only re-binds the WebView.
    /// </summary>
    private void WireWebViewCore(bool firstBoot)
    {
        var core = WebView.CoreWebView2
            ?? throw new InvalidOperationException("CoreWebView2 not ready");
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("app.local", wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Local dashboard: no form autofill state / password store to keep around.
        try { core.Settings.IsGeneralAutofillEnabled = false; } catch { /* older runtime */ }
        try { core.Settings.IsPasswordAutosaveEnabled = false; } catch { /* older runtime */ }

        _bridge?.Dispose();
        _bridge = new HostBridge(WebView, _app.Hardware, _app.Power, _app.Settings, _app.Updates, _app.AutoStart, _app.Monitor, _app);
        _bridge.Attach();
        _bridge.ExitRequested += () => Dispatcher.Invoke(() => { _exiting = true; _app.ExitApp(); });
        _bridge.MinimizeToTrayRequested += () => Dispatcher.Invoke(HideToTray);
        _bridge.GamingModeRequested += SetGamingModeFromBridgeAsync;
        _bridge.GamingModeStateRequested += GetGamingModeState;

        if (firstBoot)
        {
            _app.Monitor.MetricsUpdated += OnMetricsUpdated;
            _app.ActivePlanChanged += p => _bridge?.PushEvent(BridgeEventNames.ActivePlanChanged, new { plan = p?.PlanId, guid = p?.Guid, name = p?.Name });
            _app.Settings.SettingsChanged += s => _bridge?.PushEvent(BridgeEventNames.AutomationStateChanged, new { masterEnabled = s.MasterAutomationEnabled, @override = s.Override });
            _app.CpuAutomationStateChanged += s => _bridge?.PushEvent(BridgeEventNames.CpuAutomationStateChanged, s);
            _app.ManualOverrideChanged += o =>
            {
                _bridge?.PushEvent(BridgeEventNames.ManualOverrideChanged, new { @override = o });
                if (!IsPerformanceOverride(o, DateTime.UtcNow))
                    _gamingReminder.Stop();
                PushGamingModeState();
            };
            _app.Awake.StateChanged += s => _bridge?.PushEvent(BridgeEventNames.KeepAwakeChanged, s);
            _app.PowerSourcePlans.StateChanged += s => _bridge?.PushEvent(BridgeEventNames.PowerSourcePlanChanged, s);
            _app.ThermalGuard.StateChanged += s => { if (_webViewVisible) _bridge?.PushEvent(BridgeEventNames.ThermalGuardChanged, s); };
            _app.IdlePowerGuard.StateChanged += s => { if (_webViewVisible) _bridge?.PushEvent(BridgeEventNames.IdlePowerGuardChanged, s); };
            _app.Widgets.StateChanged += s => _bridge?.PushEvent(BridgeEventNames.WidgetsStateChanged, s);
            _app.ScheduledPowerActions.StateChanged += state =>
            {
                _bridge?.PushEvent(BridgeEventNames.ScheduledPowerActionChanged, state);
                Dispatcher.Invoke(() => RefreshScheduledPowerTrayState(state));
            };
            _hostEventsWired = true;
        }

        _webViewLifecycleBinding.Attach(core);

        NavigateToAppDocument(core);
    }

    private void AttachWebViewLifecycle(CoreWebView2 core)
    {
        core.ProcessFailed += OnWebViewProcessFailed;
        core.NavigationCompleted += OnWebViewNavigationCompleted;
    }

    private void DetachWebViewLifecycle(CoreWebView2 core)
    {
        core.ProcessFailed -= OnWebViewProcessFailed;
        core.NavigationCompleted -= OnWebViewNavigationCompleted;
    }

    private void OnWebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender is not CoreWebView2 core) return;
        if (!args.IsSuccess)
        {
            _dashboardNavigationPending = false;
            ShowDashboardErrorState();
            return;
        }

        _webViewTray.NotifyNavigationSucceeded();
        string src = core.Source ?? "";
        if (!src.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            _dashboardNavigationPending = false;
            HideDashboardStatusState();
            if (_navStopwatch.IsRunning)
            {
                Logger.Info($"NavigationCompleted in {_navStopwatch.ElapsedMilliseconds}ms (source={src})");
                _navStopwatch.Reset();
            }
            LoadUpdateSuspensionUi(core);
        }

        if (_startupToastDone) return;
        _startupToastDone = true;
        if (_justUpdated)
            _ = PushUpdatedToastAsync();
    }

    private void NavigateToAppDocument(CoreWebView2 core)
    {
        _dashboardNavigationPending = true;
        _navStopwatch.Restart();
        core.Navigate("https://app.local/index.html?v=" + AppDocumentVersion);
    }

    private static void LoadUpdateSuspensionUi(CoreWebView2 core)
    {
        _ = core.ExecuteScriptAsync(
            "(()=>{if(document.querySelector('script[data-update-suspension]'))return;" +
            "const s=document.createElement('script');s.dataset.updateSuspension='true';" +
            "s.src='js/update-suspension.js?v=suspend1';document.head.appendChild(s);})();");
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Logger.Warn($"WebView2 process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
        WebViewFailureKind kind = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited
            ? WebViewFailureKind.BrowserProcessExited
            : WebViewFailureKind.Renderer;
        _ = _webViewTray.HandleProcessFailureAsync(kind);
    }

    private async Task RecoverWebViewBrowserAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Re-initializing WebView2 after browser process exit…");
        _webViewReady = false;
        _webViewEnvironment ??= _app.WebViewEnvironment;
        cancellationToken.ThrowIfCancellationRequested();
        await WebView.EnsureCoreWebView2Async(await _webViewEnvironment);
        cancellationToken.ThrowIfCancellationRequested();
        WireWebViewCore(firstBoot: false);
        _webViewReady = true;
    }

    private void UpdateWebViewVisibility()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized && !_adaptiveFullscreenCovered;
        if (_webViewVisible == visible) return;
        if (!visible)
        {
            _webViewTray.SetVisible(false);
            return;
        }

        _ = ShowFromTrayWithFeedbackAsync(activateWindow: false);
    }

    private void OnMetricsUpdated(MetricsSnapshot metrics)
    {
        if (_webViewVisible)
            _bridge?.PushEvent(BridgeEventNames.Metrics, metrics);

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
            _app.Loc.T("Dialog_GamingReminder", currentCpu),
            _app.Loc.T("Dialog_GamingTitle"),
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
        => _bridge?.PushEvent(BridgeEventNames.GamingModeChanged, GetGamingModeState());

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
            bool applied = await Task.Run(() => _app.SetManualOverride(
                PlanId.Performance,
                null,
                "gamingManual",
                "gaming_manual"));
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

    private void ApplyHostTheme(AppThemeColor themeColor)
    {
        var color = ThemeService.GetPalette(themeColor).Background;
        Background = new Media.SolidColorBrush(color);
        WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(color.R, color.G, color.B);
    }

    private async Task PushUpdatedToastAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        string ver = _app.Updates.CurrentVersion;
        _bridge?.PushEvent(BridgeEventNames.AppUpdated, new { version = ver });
    }

    private void InitializeAutoUpdateLifecycle()
    {
        if (_app.Settings.Current.AutoUpdates.IntervalMinutes != UpdateSchedulePolicy.AutomaticCheckIntervalMinutes)
            _app.Settings.Update(state =>
                state.AutoUpdates.IntervalMinutes = UpdateSchedulePolicy.AutomaticCheckIntervalMinutes);

        _app.UpdateCoordinator.Start();
        _ = _app.UpdateCoordinator.CheckNowAsync(automatic: true);
    }

    private void OnCoordinatorUpdateAvailable(UpdateInfo info)
        => _ = Dispatcher.InvokeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return;
            if (_app.Settings.Current.AutoUpdates is { Enabled: true, SilentInstallEnabled: true })
                await PrepareUpdateInstallAsync(info.DownloadUrl);
            else if (IsAppInForeground() && _bridge != null)
                _bridge.PushEvent(BridgeEventNames.UpdateAvailable, info);
            else
                await ShowBackgroundUpdatePromptAsync(info);
        });

    private void OnCoordinatorInstallRequested(string path)
        => _ = Dispatcher.InvokeAsync(() => LaunchDownloadedInstaller(path));

    private void OnHeavyAppActivityChangedForUpdates(HeavyAppDetectionState state)
    {
        _ = Dispatcher.InvokeAsync(async () =>
            await ResumeDeferredUpdateWorkAsync(state.Active));
    }

    private void ResumeDeferredUpdateWorkAfterProtectedSession(VoltManager.Performance.ResourcePressureState state)
    {
        _ = Dispatcher.InvokeAsync(async () =>
            await ResumeDeferredUpdateWorkAsync(state.ProtectedWorkloadActive));
    }

    private async Task ResumeDeferredUpdateWorkAsync(bool active)
    {
        try
        {
            await _app.UpdateCoordinator.NotifyProtectedWorkloadChangedAsync(active);
        }
        catch (Exception ex)
        {
            ShowUpdateDownloadError(ex);
        }
    }

    internal void StopRuntime()
    {
        if (Interlocked.Exchange(ref _runtimeStopped, 1) != 0) return;
        _bridge?.Dispose();
        _app.HeavyApps.ActivityChanged -= OnHeavyAppActivityChangedForUpdates;
        _app.UpdateCoordinator.UpdateAvailable -= OnCoordinatorUpdateAvailable;
        _app.UpdateCoordinator.InstallRequested -= OnCoordinatorInstallRequested;
        _app.UpdateCoordinator.Stop();
        _webViewLifecycleBinding.Dispose();
        _webViewTray.Dispose();
        _hotkeySource?.RemoveHook(GlobalHotkeyWndProc);
        _globalHotkeys.Dispose();
    }

    private bool IsAppInForeground()
        => IsVisible && WindowState != WindowState.Minimized && IsActive;

    private async Task ShowBackgroundUpdatePromptAsync(UpdateInfo info)
    {
        if (_updatePromptOpen) return;
        _updatePromptOpen = true;
        try
        {
            var prompt = new UpdatePromptWindow(info, _app.Loc);
            if (IsVisible) prompt.Owner = this;
            prompt.Icon = Icon;
            prompt.ShowDialog();

            switch (prompt.Action)
            {
                case UpdatePromptAction.Install:
                    await PrepareUpdateInstallAsync(info.DownloadUrl!);
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

    private async Task PrepareUpdateInstallAsync(string url)
    {
        try
        {
            await _app.UpdateCoordinator.PrepareInstallAsync(
                url,
                (downloadUrl, cancellationToken) => _app.Updates.DownloadUpdateAsync(downloadUrl, cancellationToken));
        }
        catch (Exception ex)
        {
            ShowUpdateDownloadError(ex);
        }
    }

    private void LaunchDownloadedInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path,
                $"/update --pid {Environment.ProcessId} --lang {_app.Loc.CurrentLanguage}") { UseShellExecute = true });
            _exiting = true;
            _app.ExitApp();
        }
        catch (Exception ex)
        {
            ShowUpdateDownloadError(ex);
        }
    }

    private void ShowUpdateDownloadError(Exception ex)
    {
        Logger.Error("Update download/install failed", ex);
        MessageBox.Show(_app.Loc.T("Dialog_UpdateDownloadFailed", ex.Message),
            _app.Loc.T("Dialog_VoltManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SnoozeUpdate(int minutes)
        => _app.UpdateCoordinator.Snooze(minutes);

    private void SkipUpdateVersion(string? version)
        => _app.UpdateCoordinator.SkipVersion(version);

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
        => _webViewTray.HideToTray();

    public void ShowFromTray()
        => _ = ShowFromTrayWithFeedbackAsync();

    private async Task ShowFromTrayWithFeedbackAsync(bool activateWindow = true)
    {
        Task restore = _webViewTray.ShowFromTrayAsync(activateWindow);
        bool feedbackVisible = false;

        try
        {
            if (!_webViewReady || _dashboardNavigationPending)
            {
                ShowDashboardLoadingState();
                ShowWindowShell();
                feedbackVisible = true;
            }
            else if (!restore.IsCompleted)
            {
                Task first = await Task.WhenAny(restore, Task.Delay(SlowRestoreFeedbackDelay));
                if (first != restore && !restore.IsCompleted)
                {
                    ShowDashboardLoadingState();
                    ShowWindowShell();
                    feedbackVisible = true;
                }
            }

            await restore;
            if (feedbackVisible && !_dashboardNavigationPending && !_dashboardLoadFailed)
                HideDashboardStatusState();
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("Dashboard restore failed", ex);
            ShowDashboardErrorState();
            ShowWindowShell();
        }
    }

    private void ShowWindowShell()
    {
        if (_exiting) return;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowDashboardLoadingState()
    {
        _dashboardLoadFailed = false;
        DashboardStatusMessage.Text = "Caricamento dell'interfaccia…";
        DashboardLoadingIndicator.Visibility = Visibility.Visible;
        DashboardRetryButton.Visibility = Visibility.Collapsed;
        DashboardStatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowDashboardErrorState()
    {
        _dashboardLoadFailed = true;
        WebView.Visibility = Visibility.Hidden;
        DashboardStatusMessage.Text = "Impossibile caricare la dashboard. Premi Riprova per tentare nuovamente.";
        DashboardLoadingIndicator.Visibility = Visibility.Collapsed;
        DashboardRetryButton.Visibility = Visibility.Visible;
        DashboardStatusPanel.Visibility = Visibility.Visible;
    }

    private void HideDashboardStatusState()
    {
        _dashboardLoadFailed = false;
        DashboardStatusPanel.Visibility = Visibility.Collapsed;
        DashboardRetryButton.Visibility = Visibility.Collapsed;
    }

    private async void DashboardRetry_Click(object sender, RoutedEventArgs e)
    {
        ShowDashboardLoadingState();
        try
        {
            if (_webViewReady && WebView.CoreWebView2 is { } core)
            {
                _dashboardNavigationPending = true;
                WebView.Visibility = Visibility.Visible;
                core.Reload();
                return;
            }

            await ShowFromTrayWithFeedbackAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Dashboard retry failed", ex);
            ShowDashboardErrorState();
        }
    }

    /// <summary>Applies localized strings to tray menu items with x:Name in XAML.</summary>
    private void LocalizeTrayMenu()
    {
        var loc = _app.Loc;
        TrayIcon.ToolTipText = "VoltManager – " + PlanDisplayName(_app.ActivePlan);
        TrayOpenItem.Header = loc.T("Tray_Open");
        TrayPowerPlanCategoryItem.Header = loc.T("Tray_PowerPlanCategory");
        TrayGamingPlanItem.Header = loc.T("Tray_GamingPlan");
        TrayChangePlanItem.Header = loc.T("Tray_ChangePlan");
        TrayPlanSaverItem.Header = loc.T("Tray_PlanSaver");
        TrayPlanBalancedItem.Header = loc.T("Tray_PlanBalanced");
        TrayPlanPerfItem.Header = loc.T("Tray_PlanPerformance");
        TrayPlanSaver1h.Header = loc.T("Tray_Duration1h");
        TrayPlanSaver10h.Header = loc.T("Tray_Duration10h");
        TrayPlanSaver12h.Header = loc.T("Tray_Duration12h");
        TrayPlanSaverForever.Header = loc.T("Tray_DurationForever");
        TrayBalanced1h.Header = loc.T("Tray_Duration1h");
        TrayBalanced10h.Header = loc.T("Tray_Duration10h");
        TrayBalanced12h.Header = loc.T("Tray_Duration12h");
        TrayBalancedForever.Header = loc.T("Tray_DurationForever");
        TrayPerf1h.Header = loc.T("Tray_Duration1h");
        TrayPerf10h.Header = loc.T("Tray_Duration10h");
        TrayPerf12h.Header = loc.T("Tray_Duration12h");
        TrayPerfForever.Header = loc.T("Tray_DurationForever");
        TrayAutomationItem.Header = loc.T("Tray_Automation");
        TrayClearOverrideItem.Header = loc.T("Tray_ClearOverride");
        TrayPcControlsCategoryItem.Header = loc.T("Tray_PcControlsCategory");
        TrayKeepAwakeItem.Header = loc.T("Tray_KeepAwake");
        TraySchedulePowerItem.Header = loc.T("Tray_Schedule");
        TraySchedule30mItem.Header = loc.T("Tray_30min");
        TraySchedule45mItem.Header = loc.T("Tray_45min");
        TraySchedule1hItem.Header = loc.T("Tray_1hour");
        TraySchedule2hItem.Header = loc.T("Tray_2hours");
        TraySchedule4hItem.Header = loc.T("Tray_4hours");
        TraySchedule30mShutdownItem.Header = loc.T("Tray_ScheduleShutdown");
        TraySchedule45mShutdownItem.Header = loc.T("Tray_ScheduleShutdown");
        TraySchedule1hShutdownItem.Header = loc.T("Tray_ScheduleShutdown");
        TraySchedule2hShutdownItem.Header = loc.T("Tray_ScheduleShutdown");
        TraySchedule4hShutdownItem.Header = loc.T("Tray_ScheduleShutdown");
        TraySchedule30mSleepItem.Header = loc.T("Tray_ScheduleSleep");
        TraySchedule45mSleepItem.Header = loc.T("Tray_ScheduleSleep");
        TraySchedule1hSleepItem.Header = loc.T("Tray_ScheduleSleep");
        TraySchedule2hSleepItem.Header = loc.T("Tray_ScheduleSleep");
        TraySchedule4hSleepItem.Header = loc.T("Tray_ScheduleSleep");
        TrayScheduleCustomItem.Header = loc.T("Tray_ScheduleCustom");
        TrayCancelScheduledItem.Header = loc.T("Tray_CancelScheduled");
        TrayExitItem.Header = loc.T("Tray_Exit");
        RefreshScheduledPowerTrayState(_app.ScheduledPowerActions.GetState());
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private string PlanDisplayName(Models.PowerPlan? plan) => plan?.PlanId switch
    {
        PlanId.PowerSaver => _app.Loc.T("Plan_Saver"),
        PlanId.Balanced => _app.Loc.T("Plan_Balanced"),
        PlanId.Performance => _app.Loc.T("Plan_Performance"),
        _ => string.IsNullOrEmpty(plan?.Name) ? _app.Loc.T("Plan_Unknown") : plan.Name,
    };

    private void TrayMenu_Opened(object sender, RoutedEventArgs e)
    {
        TrayActivePlanItem.Header = _app.Loc.T("Tray_ActivePlan", PlanDisplayName(_app.ActivePlan));
        TrayGamingPlanItem.IsChecked = IsGamingModeActive();
        TrayKeepAwakeItem.IsChecked = _app.Awake.GetState().Enabled;
        TrayAutomationItem.IsChecked = _app.Settings.Current.MasterAutomationEnabled;
        TrayClearOverrideItem.Visibility = _app.Settings.Current.Override != null
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshScheduledPowerTrayState(_app.ScheduledPowerActions.GetState());
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
            _app.Loc.T("Dialog_GamingActivationFailed"),
            _app.Loc.T("Dialog_GamingModeTitle"),
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
        _app.Settings.Update(state =>
            state.MasterAutomationEnabled = TrayAutomationItem.IsChecked);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        // Warn if a relative schedule is active; closing exits the process so the timer dies.
        var state = _app.ScheduledPowerActions.GetState();
        if (state.Enabled && state.Mode == ScheduledPowerMode.Relative)
        {
            var result = MessageBox.Show(
                _app.Loc.T("Dialog_ScheduleExitWarning"),
                _app.Loc.T("Dialog_VoltManagerTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
            _app.ScheduledPowerActions.Cancel();
        }
        _exiting = true;
        _app.ExitApp();
    }

    // -- Tray schedule menu (dynamic, built in XAML generation or code) --

    private void TraySchedulePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag })
            return;

        string[] parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int minutes))
            return;

        if (!Enum.TryParse<ScheduledPowerActionType>(parts[0], ignoreCase: true, out var action))
            return;

        if (!ConfirmScheduleReplacement())
            return;

        _ = Task.Run(() => _app.ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(minutes), action));
    }

    private void TrayScheduleCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmScheduleReplacement())
            return;

        var dialog = new SchedulePowerActionWindow(_app.Loc);
        dialog.Owner = this;
        dialog.Icon = Icon;
        if (dialog.ShowDialog() != true)
            return;

        _ = Task.Run(() => _app.ScheduledPowerActions.ScheduleAfter(dialog.SelectedDelay, dialog.SelectedAction));
    }

    private void TrayCancelScheduled_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() => _app.ScheduledPowerActions.Cancel());
    }

    private bool ConfirmScheduleReplacement()
    {
        var current = _app.ScheduledPowerActions.GetState();
        if (!current.Enabled)
            return true;

        return MessageBox.Show(
            _app.Loc.T("Dialog_ReplaceScheduledAction"),
            _app.Loc.T("Dialog_VoltManagerTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void RefreshScheduledPowerTrayState(ScheduledPowerActionState state)
    {
        TrayCancelScheduledItem.Visibility = state.Enabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!state.Enabled)
        {
            TrayScheduledStateItem.Header = _app.Loc.T("Tray_NoScheduledAction");
            return;
        }

        TrayScheduledStateItem.Header = BuildScheduledActionTrayText(state);
    }

    private string BuildScheduledActionTrayText(ScheduledPowerActionState state)
    {
        string actionName = state.Action switch
        {
            ScheduledPowerActionType.Shutdown => _app.Loc.T("Schedule_Shutdown"),
            ScheduledPowerActionType.Sleep => _app.Loc.T("Schedule_Sleep"),
            ScheduledPowerActionType.Restart => _app.Loc.T("Schedule_Restart"),
            _ => state.Action.ToString(),
        };

        if (state.Mode == ScheduledPowerMode.Relative && state.RemainingSeconds > 0)
        {
            var remaining = TimeSpan.FromSeconds(state.RemainingSeconds);
            string timeText = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}min"
                : $"{remaining.Minutes}min";
            return $"{actionName} {_app.Loc.T("Tray_ScheduledIn")} {timeText}";
        }

        if (state.Mode == ScheduledPowerMode.Daily && state.DailyTime != null)
            return $"{actionName} {_app.Loc.T("Tray_ScheduledAt")} {state.DailyTime}";

        return actionName;
    }

    private sealed class DashboardSurface : IDashboardSurface
    {
        private readonly MainWindow _owner;

        public DashboardSurface(MainWindow owner) => _owner = owner;

        public bool IsVisible => _owner._webViewVisible;

        public void HideWindow() => Run(() =>
        {
            _owner.Hide();
            _owner.ShowInTaskbar = false;
        });

        public void ShowAndActivateWindow() => Run(_owner.ShowWindowShell);

        public Task EnsureWebViewAsync(CancellationToken cancellationToken)
            => RunAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _owner.EnsureWebViewAsync();
                cancellationToken.ThrowIfCancellationRequested();
            });

        public void SetWebViewVisible(bool visible) => Run(() =>
        {
            _owner._webViewVisible = visible;
            _owner.WebView.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        });

        public Task<bool> SuspendAsync(CancellationToken cancellationToken)
            => RunAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CoreWebView2? core = _owner.WebView.CoreWebView2;
                if (core == null || _owner._webViewVisible) return false;
                _owner.WebView.Visibility = Visibility.Hidden;
                bool suspended = await core.TrySuspendAsync();
                cancellationToken.ThrowIfCancellationRequested();
                return suspended;
            });

        public void Resume() => Run(() =>
        {
            if (_owner._exiting) return;
            _owner.WebView.CoreWebView2?.Resume();
        });

        public void NavigateApp() => Run(() =>
        {
            if (!_owner._webViewVisible || _owner._exiting) return;
            CoreWebView2? core = _owner.WebView.CoreWebView2;
            if (_owner._webViewReady && core != null &&
                (string.IsNullOrEmpty(core.Source) || core.Source.StartsWith("about:", StringComparison.OrdinalIgnoreCase)))
                _owner.NavigateToAppDocument(core);
        });

        public void Reload() => Run(() =>
        {
            try { _owner.WebView.CoreWebView2?.Reload(); }
            catch (Exception ex) { Logger.Error("WebView reload after crash failed", ex); }
        });

        public void ShowLoading()
            => Run(_owner.ShowDashboardLoadingState);

        public void ShowLoadError()
            => Run(_owner.ShowDashboardErrorState);

        public Task RecoverBrowserAsync(CancellationToken cancellationToken)
            => RunAsync(() => _owner.RecoverWebViewBrowserAsync(cancellationToken));

        public void PublishFreshState() => Run(() =>
        {
            _owner.PublishFreshAdaptiveStateAfterResume();
            _owner._app.RefreshHardwareSamplingDemand(requestFresh: true);
        });

        private void Run(Action action)
        {
            if (_owner.Dispatcher.CheckAccess()) action();
            else _owner.Dispatcher.Invoke(action);
        }

        private Task RunAsync(Func<Task> action)
            => _owner.Dispatcher.CheckAccess()
                ? action()
                : _owner.Dispatcher.InvokeAsync(action).Task.Unwrap();

        private Task<T> RunAsync<T>(Func<Task<T>> action)
            => _owner.Dispatcher.CheckAccess()
                ? action()
                : _owner.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    /// <summary>Navigate WebView to the system/schedule section.</summary>
    public void NavigateToSystemView()
    {
        try
        {
            WebView.CoreWebView2?.ExecuteScriptAsync(
                "document.querySelector('[data-view=\"system\"]')?.click()");
        }
        catch { /* WebView may not be ready */ }
    }
}
