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
    private System.Threading.Timer? _autoUpdateTimer;
    private int _autoUpdateCheckRunning;
    private bool _updatePromptOpen;
    private readonly GamingModeReminderService _gamingReminder = new();
    private int _gamingReminderPromptRunning;
    private int _rendererReloadCount;
    private bool _hostEventsWired;
    private bool _webViewRecovering;
    private volatile bool _webViewVisible;
    private readonly bool _startMinimized;
    private bool _webViewReady;
    private int _webViewInitRunning;
    private System.Threading.Timer? _trayTeardownTimer;
    private System.Threading.Timer? _workingSetTrimTimer;
    private readonly MemoryOptimizerService _memoryOptimizer = new();
    // Stable document version for HTTP/V8 code cache across tray reopens (not wall-clock).
    private static readonly string AppDocumentVersion =
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly Stopwatch _navStopwatch = new();
    private readonly GlobalHotkeyService _globalHotkeys = new();
    private HwndSource? _hotkeySource;

    // After this park time in tray, drop the page to about:blank so Chromium
    // releases DOM/JS/GPU tiles. Reopened UI reloads fresh (same as cold open).
    private static readonly TimeSpan TrayTeardownDelay = TimeSpan.FromSeconds(20);
    // Grace after the blank so Chromium has actually released before the OS trim.
    private static readonly TimeSpan WorkingSetTrimDelay = TimeSpan.FromSeconds(5);

    public MainWindow(App app, bool startMinimized, bool justUpdated = false,
        Task<CoreWebView2Environment>? webViewEnvironment = null)
    {
        _app = app;
        // May stay null until EnsureWebViewAsync — tray-only sessions skip Chromium.
        _webViewEnvironment = webViewEnvironment;
        _justUpdated = justUpdated;
        _startMinimized = startMinimized;
        InitializeComponent();
        SourceInitialized += (_, _) => BindGlobalHotkeys();
        ApplyHostTheme(_app.Theme.CurrentTheme);
        // Tray-only launch: keep Chromium unborn until the user opens the window.
        Loaded += async (_, _) =>
        {
            if (!_startMinimized || IsVisible && WindowState != WindowState.Minimized)
                await EnsureWebViewAsync();
        };
        IsVisibleChanged += (_, _) => UpdateWebViewVisibility();
        StateChanged += (_, _) => UpdateWebViewVisibility();
        Closing += OnClosingToTray;
        Closed += (_, _) =>
        {
            _autoUpdateTimer?.Dispose();
            _trayTeardownTimer?.Dispose();
            _workingSetTrimTimer?.Dispose();
            _hotkeySource?.RemoveHook(GlobalHotkeyWndProc);
            _globalHotkeys.Dispose();
        };
        // Fires from timer threads; tooltip lives on the UI thread.
        _app.ActivePlanChanged += p => Dispatcher.Invoke(() =>
            TrayIcon.ToolTipText = "VoltManager – " + PlanDisplayName(p));
        _app.Settings.SettingsChanged += s => Dispatcher.Invoke(() =>
        {
            _app.Theme.SetTheme(s.ThemeColor);
            // Keep the main WebView font in sync with disk (import/other writers).
            _bridge?.PushEvent("fontChanged", new { font = s.Font });
            BindGlobalHotkeys();
        });
        _app.Theme.ThemeChanged += themeColor => Dispatcher.Invoke(() =>
        {
            ApplyHostTheme(themeColor);
            _bridge?.PushEvent("themeChanged", _app.Theme.GetWebTheme());
        });
        _app.Loc.LanguageChanged += (code, culture) => Dispatcher.Invoke(() =>
        {
            LocalizeTrayMenu();
            _app.Widgets.PushLanguage();
            _bridge?.PushEvent("languageChanged", new { language = code, locale = culture.Name });
        });
        _app.HeavyApps.ActivityChanged += OnHeavyAppActivityChangedForUpdates;
        LocalizeTrayMenu();
        InitializeAutoUpdateLifecycle();

        if (startMinimized)
        {
            // Window object exists for tray lifetime; WebView stays uncreated.
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Show();
            Hide();
            ScheduleWorkingSetTrim();
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

        var registrations = _globalHotkeys.Rebind(hwnd, _app.Settings.Current.GlobalHotkeys);
        _bridge?.PushEvent("globalHotkeysChanged", new { registrations });
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
            MessageBox.Show(
                _app.Loc.T("Dialog_WebView2Missing", ex.Message),
                _app.Loc.T("Dialog_VoltManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            _app.ExitApp();
            return;
        }

        try
        {
            WireWebViewCore(firstBoot: !_hostEventsWired);
            _webViewReady = true;
        }
        catch (Exception ex)
        {
            // WebView came up but wiring the UI failed: the dashboard is unusable,
            // so report it and exit cleanly rather than leaving a blank window.
            Logger.Error("WebView UI setup failed", ex);
            MessageBox.Show(
                _app.Loc.T("Dialog_WebView2SetupFailed", ex.Message),
                _app.Loc.T("Dialog_VoltManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            _app.ExitApp();
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

        _bridge = new HostBridge(WebView, _app.Hardware, _app.Power, _app.Settings, _app.Updates, _app.AutoStart, _app.Monitor, _app);
        _bridge.Attach();
        _bridge.ExitRequested += () => Dispatcher.Invoke(() => { _exiting = true; _app.ExitApp(); });
        _bridge.MinimizeToTrayRequested += () => Dispatcher.Invoke(HideToTray);
        _bridge.GamingModeRequested += SetGamingModeFromBridgeAsync;
        _bridge.GamingModeStateRequested += GetGamingModeState;

        if (firstBoot)
        {
            _app.Monitor.MetricsUpdated += OnMetricsUpdated;
            _app.ActivePlanChanged += p => _bridge?.PushEvent("activePlanChanged", new { plan = p?.PlanId, guid = p?.Guid, name = p?.Name });
            _app.Settings.SettingsChanged += s => _bridge?.PushEvent("automationStateChanged", new { masterEnabled = s.MasterAutomationEnabled, @override = s.Override });
            _app.CpuAutomationStateChanged += s => _bridge?.PushEvent("cpuAutomationStateChanged", s);
            _app.ManualOverrideChanged += o =>
            {
                _bridge?.PushEvent("manualOverrideChanged", new { @override = o });
                if (!IsPerformanceOverride(o, DateTime.UtcNow))
                    _gamingReminder.Stop();
                PushGamingModeState();
            };
            _app.Awake.StateChanged += s => _bridge?.PushEvent("keepAwakeChanged", s);
            _app.PowerSourcePlans.StateChanged += s => _bridge?.PushEvent("powerSourcePlanChanged", s);
            _app.ThermalGuard.StateChanged += s => _bridge?.PushEvent("thermalGuardChanged", s);
            _app.IdlePowerGuard.StateChanged += s => _bridge?.PushEvent("idlePowerGuardChanged", s);
            _app.Widgets.StateChanged += s => _bridge?.PushEvent("widgetsStateChanged", s);
            _app.ScheduledPowerActions.StateChanged += state =>
            {
                _bridge?.PushEvent("scheduledPowerActionChanged", state);
                Dispatcher.Invoke(() => RefreshScheduledPowerTrayState(state));
            };
            _hostEventsWired = true;
        }

        core.ProcessFailed += OnWebViewProcessFailed;

        bool startupToastDone = false;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            _rendererReloadCount = 0; // a clean load means the renderer recovered
            string src = core.Source ?? "";
            if (!src.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                if (_navStopwatch.IsRunning)
                {
                    Logger.Info($"NavigationCompleted in {_navStopwatch.ElapsedMilliseconds}ms (source={src})");
                    _navStopwatch.Reset();
                }
                LoadUpdateSuspensionUi(core);
            }
            if (startupToastDone) return;
            startupToastDone = true;
            if (_justUpdated)
                _ = PushUpdatedToastAsync();
        };

        NavigateToAppDocument(core);
    }

    private void NavigateToAppDocument(CoreWebView2 core)
    {
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
        // Under memory pressure the OS can kill the WebView2 renderer; without this
        // the dashboard just goes blank and the app looks crashed. Reload so it
        // self-heals. Cap retries so a renderer that keeps dying can't spin forever.
        Logger.Warn($"WebView2 process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
        if (Interlocked.Increment(ref _rendererReloadCount) > 5)
        {
            Logger.Error("WebView2 renderer kept failing; giving up auto-reload.");
            return;
        }

        // Browser process exit kills CoreWebView2; re-create host without stacking
        // app-level event handlers.
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            if (_webViewRecovering) return;
            _webViewRecovering = true;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    Logger.Info("Re-initializing WebView2 after browser process exit…");
                    _webViewEnvironment ??= _app.WebViewEnvironment;
                    await WebView.EnsureCoreWebView2Async(await _webViewEnvironment);
                    WireWebViewCore(firstBoot: false);
                }
                catch (Exception ex)
                {
                    Logger.Error("WebView re-init after browser exit failed", ex);
                }
                finally
                {
                    _webViewRecovering = false;
                }
            });
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            try { WebView.CoreWebView2?.Reload(); }
            catch (Exception ex) { Logger.Error("WebView reload after crash failed", ex); }
        });
    }

    private void UpdateWebViewVisibility()
        => _webViewVisible = IsVisible && WindowState != WindowState.Minimized;

    private void OnMetricsUpdated(MetricsSnapshot metrics)
    {
        if (_webViewVisible)
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
        _bridge?.PushEvent("appUpdated", new { version = ver });
    }

    private void InitializeAutoUpdateLifecycle()
    {
        _app.Settings.Current.AutoUpdates ??= new AutoUpdateSettings();
        if (_app.Settings.Current.AutoUpdates.IntervalMinutes != UpdateSchedulePolicy.AutomaticCheckIntervalMinutes)
        {
            _app.Settings.Current.AutoUpdates.IntervalMinutes = UpdateSchedulePolicy.AutomaticCheckIntervalMinutes;
            _app.Settings.Save();
        }

        StartAutoUpdateLoop();
        _ = CheckForUpdatesOnStartupAsync();
    }

    private Task CheckForUpdatesOnStartupAsync()
        => RunAutoUpdateCheckAsync();

    private void StartAutoUpdateLoop()
    {
        var interval = GetAutoUpdateInterval();
        _autoUpdateTimer = new System.Threading.Timer(_ =>
        {
            _ = Dispatcher.InvokeAsync(async () => await RunAutoUpdateCheckAsync());
        }, null, interval, interval);
    }

    private static TimeSpan GetAutoUpdateInterval()
        => UpdateSchedulePolicy.AutomaticCheckInterval;

    private async Task RunAutoUpdateCheckAsync()
    {
        if (Interlocked.Exchange(ref _autoUpdateCheckRunning, 1) == 1) return;

        try
        {
            var autoUpdates = _app.Settings.Current.AutoUpdates;
            if (!UpdateSchedulePolicy.IsAutomaticCheckAllowed(autoUpdates, DateTime.UtcNow)) return;

            var info = await _app.Updates.CheckForUpdatesAsync();
            if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl)) return;
            if (IsUpdateSuppressed(info, respectSnooze: true)) return;

            // Never install or interrupt while a game is running.
            if (_app.IsHeavyAppSessionActive())
            {
                if (ShouldInstallUpdatesSilently())
                    _app.DeferUpdateUntilGameEnds(info.DownloadUrl);
                Logger.Info("Automatic update deferred: game/heavy app session active.");
                return;
            }

            if (ShouldInstallUpdatesSilently())
                await DownloadAndInstallUpdateAsync(info.DownloadUrl);
            else if (IsAppInForeground() && _bridge != null)
                _bridge.PushEvent("updateAvailable", info);
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

    private void OnHeavyAppActivityChangedForUpdates(HeavyAppDetectionState state)
    {
        // ActivityChanged may fire from the detection timer thread.
        if (state.Active) return;
        if (!_app.HasDeferredUpdate()) return;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            // Re-check on the UI thread: another scan may have reactivated the session.
            if (_app.IsHeavyAppSessionActive()) return;
            string? url = _app.TakeDeferredUpdateUrl();
            if (string.IsNullOrWhiteSpace(url)) return;

            Logger.Info("Game/heavy app session ended — installing deferred update.");
            await DownloadAndInstallUpdateAsync(url);
        });
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
            var prompt = new UpdatePromptWindow(info, _app.Loc);
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
        // Last-line guard: never restart the host while a game is running.
        if (_app.IsHeavyAppSessionActive())
        {
            _app.DeferUpdateUntilGameEnds(url);
            return;
        }

        try
        {
            string path = await _app.Updates.DownloadUpdateAsync(url);
            // Game may have started during download — re-check before launching installer.
            if (_app.IsHeavyAppSessionActive())
            {
                _app.DeferUpdateUntilGameEnds(url);
                return;
            }

            Process.Start(new ProcessStartInfo(path,
                $"/update --pid {Environment.ProcessId} --lang {_app.Loc.CurrentLanguage}") { UseShellExecute = true });
            _exiting = true;
            _app.ExitApp();
        }
        catch (Exception ex)
        {
            Logger.Error("Update download/install failed", ex);
            MessageBox.Show(_app.Loc.T("Dialog_UpdateDownloadFailed", ex.Message),
                _app.Loc.T("Dialog_VoltManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SnoozeUpdate(int minutes)
    {
        minutes = UpdateSchedulePolicy.NormalizeSnoozeMinutes(minutes);
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
        _webViewVisible = false;
        // Suspending is the single WebView2 memory policy while hidden. It pauses
        // script timers/animations and lowers renderer memory without mixing APIs.
        TrySuspendWebView();
        ScheduleTrayTeardown();
    }

    public void ShowFromTray()
    {
        CancelTrayTeardown();
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await EnsureWebViewAsync();
            ResumeWebView();
            // If tray teardown blanked the page, put the dashboard back.
            try
            {
                var core = WebView.CoreWebView2;
                if (core != null &&
                    (string.IsNullOrEmpty(core.Source) ||
                     core.Source.StartsWith("about:", StringComparison.OrdinalIgnoreCase)))
                {
                    NavigateToAppDocument(core);
                }
            }
            catch (Exception ex) { Logger.Warn("WebView restore after tray failed: " + ex.Message); }
        });
    }

    private void TrySuspendWebView()
    {
        try
        {
            // Best-effort: pause renderer work and let WebView2 lower its own memory target.
            _ = WebView.CoreWebView2?.TrySuspendAsync();
        }
        catch (Exception ex) { Logger.Warn("WebView TrySuspend failed: " + ex.Message); }
    }

    private void ResumeWebView()
    {
        try { WebView.CoreWebView2?.Resume(); }
        catch (Exception ex) { Logger.Warn("WebView Resume failed: " + ex.Message); }
    }

    private void ScheduleTrayTeardown()
    {
        _trayTeardownTimer?.Dispose();
        _trayTeardownTimer = new System.Threading.Timer(_ =>
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_webViewVisible || _exiting) return;
                try
                {
                    // Navigate auto-resumes a suspended WebView. Re-suspend immediately
                    // after dropping DOM/JS heap + most GPU tiles to keep the tray state lean.
                    WebView.CoreWebView2?.Navigate("about:blank");
                    TrySuspendWebView();
                    Logger.Info("WebView blanked after tray park.");
                }
                catch (Exception ex) { Logger.Warn("Tray WebView teardown failed: " + ex.Message); }
                ScheduleWorkingSetTrim();
            });
        }, null, TrayTeardownDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Chromium releases its allocations asynchronously after the blank + suspend, so the
    /// OS-level trim runs a few seconds later — otherwise it would hand back pages that
    /// are about to be freed anyway and miss the ones that matter.
    /// </summary>
    private void ScheduleWorkingSetTrim()
    {
        _workingSetTrimTimer?.Dispose();
        _workingSetTrimTimer = new System.Threading.Timer(_ =>
        {
            if (_webViewVisible || _exiting) return;
            try
            {
                int trimmed = _memoryOptimizer.TrimParkedWorkingSets();
                if (trimmed > 0) Logger.Info($"Working set released for {trimmed} parked process(es).");
            }
            catch (Exception ex) { Logger.Warn("Tray working-set trim failed: " + ex.Message); }
        }, null, WorkingSetTrimDelay, Timeout.InfiniteTimeSpan);
    }

    private void CancelTrayTeardown()
    {
        _trayTeardownTimer?.Dispose();
        _trayTeardownTimer = null;
        _workingSetTrimTimer?.Dispose();
        _workingSetTrimTimer = null;
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
        _app.Settings.Current.MasterAutomationEnabled = TrayAutomationItem.IsChecked;
        _app.Settings.Save();
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
