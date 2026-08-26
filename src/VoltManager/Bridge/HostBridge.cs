using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Bridge;

/// <summary>
/// JSON-RPC over WebView2 postMessage.
/// JS sends {id, method, payload}; C# replies {id, ok, result|error}.
/// C# pushes events as {event, data}.
/// </summary>
public class HostBridge
{
    // Shared with BridgeRpc so reply JSON and dispatch serialization stay identical.
    private static readonly JsonSerializerOptions JsonOpts = BridgeRpc.JsonOpts;

    // Same shape as settings.json on disk (PascalCase, enum names), so a backup
    // is interchangeable with the real settings file.
    private static readonly JsonSerializerOptions BackupJsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WebView2 _webView;
    private readonly HardwareInfoService _hardware;
    private readonly PowerPlanService _power;
    private readonly SettingsService _settings;
    private readonly UpdateService _updates;
    private readonly StartupService _startup;
    private readonly StartupAppsService _startupApps = new();
    private readonly PowerPlanParameterService _planParams;
    private readonly MemoryOptimizerService _memoryOptimizer;
    private readonly BatteryHealthService _batteryHealth = new();
    private readonly PowerFlowService _powerFlow = new();
    private readonly BatteryPowerSmoother _batteryPowerSmoother = new();
    private readonly MonitorService _monitor;
    private readonly App _app;
    private readonly LocalizationService _loc;
    private readonly bool _subscribeGlobalEvents;

    public event Action? ExitRequested;
    public event Action? MinimizeToTrayRequested;
    public event Func<bool, Task<object?>>? GamingModeRequested;
    public event Func<object?>? GamingModeStateRequested;
    public event Action? WidgetDragRequested;
    public event Action<bool>? WidgetTopmostRequested;
    public event Action? WidgetCloseRequested;

    public HostBridge(WebView2 webView, HardwareInfoService hardware, PowerPlanService power,
        SettingsService settings, UpdateService updates, StartupService startup, MonitorService monitor, App app,
        bool subscribeGlobalEvents = true)
    {
        _webView = webView;
        _hardware = hardware;
        _power = power;
        _settings = settings;
        _updates = updates;
        _startup = startup;
        _monitor = monitor;
        _planParams = new PowerPlanParameterService(power);
        _memoryOptimizer = new MemoryOptimizerService();
        _app = app;
        _loc = app.Loc;
        _subscribeGlobalEvents = subscribeGlobalEvents;
    }

    public void Attach()
    {
        _webView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            string json;
            try { json = e.WebMessageAsJson; }
            catch (Exception ex)
            {
                // Reading the raw message can throw if the payload is malformed;
                // never let it surface as an unhandled async-void exception.
                Logger.Error("Could not read web message", ex);
                return;
            }
            await HandleMessageAsync(json);
        };

        if (!_subscribeGlobalEvents) return;

        _updates.DownloadProgress += pct => PushEvent("updateDownloadProgress", new { pct });
        _app.HeavyApps.ActivityChanged += state => PushEvent("heavyAppActivityChanged", state);
        _app.AppProfiles.ActivityChanged += state => PushEvent("appPowerProfileActivityChanged", state);
        _app.PowerPlanConflictDetected += notice => PushEvent("powerPlanConflictDetected", notice);
        _app.StandbyAutoCleaner.AutoCleaned += freshMem => PushEvent("standbyAutoCleaned", freshMem);
    }

    private bool _pushEventFaulted;

    public void PushEvent(string name, object data)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { @event = name, data }, JsonOpts);
            _webView.Dispatcher.Invoke(() =>
            {
                try { _webView.CoreWebView2?.PostWebMessageAsJson(payload); }
                catch { /* WebView torn down mid-push */ }
            });
            _pushEventFaulted = false;
        }
        catch (Exception ex)
        {
            // Serialization bugs or a shutting-down dispatcher must not crash the
            // event source; log the first failure of a streak only.
            _pushEventFaulted = Logger.WarnOnce(_pushEventFaulted, "PushEvent failed: " + name, ex);
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            string method = root.GetProperty("method").GetString() ?? "";
            JsonElement payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

            object? result = await DispatchAsync(method, payload);
            PostReplyJson(BridgeRpc.FormatSuccess(id!, result));
        }
        catch (Exception ex)
        {
            // Dispatch failures are non-fatal: log + non-ok reply; process stays up.
            var failure = BridgeRpc.OnDispatchException(id, ex);
            Logger.Error(failure.LogMessage, ex);
            if (failure.ShouldReply)
                PostReplyJson(BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage));
        }
    }

    private void PostReplyJson(string msg)
    {
        _webView.Dispatcher.Invoke(() =>
        {
            try { _webView.CoreWebView2?.PostWebMessageAsJson(msg); }
            catch { /* WebView torn down mid-reply */ }
        });
    }

    private async Task<object?> DispatchAsync(string method, JsonElement payload)
    {
        switch (method)
        {
            case "getSystemInfo":
                // WMI (GPU/CPU fallback) can stall; keep first render off the UI thread.
                return await Task.Run(() => _hardware.GetSystemInfo());

            case "getBatteryHealth":
                return await Task.Run(() => _batteryHealth.GetHealth());

            case "getBatteryPower":
                return await Task.Run(() =>
                {
                    var raw = _powerFlow.GetState();
                    // Stabilize noisy WMI rates and attach session Wh from history.
                    IReadOnlyList<BatteryHistorySample>? history = null;
                    try { history = _app.BatteryHistory.GetHistory(); }
                    catch { /* history is best-effort enrichment */ }
                    return _batteryPowerSmoother.Apply(raw, history, DateTime.UtcNow);
                });

            case "getBatteryHistory":
                // Sparkline is ~100 CSS-px wide: ship at most 96 points (evenly
                // sampled) instead of up to 2880. Same visual, far less JSON/GC.
                return await Task.Run(() =>
                {
                    var all = _app.BatteryHistory.GetHistory();
                    const int maxPts = 96;
                    if (all.Count <= maxPts)
                        return new { samples = all };
                    var slim = new List<BatteryHistorySample>(maxPts);
                    int last = all.Count - 1;
                    for (int i = 0; i < maxPts; i++)
                        slim.Add(all[(int)Math.Round(i * (double)last / (maxPts - 1))]);
                    return new { samples = (IReadOnlyList<BatteryHistorySample>)slim };
                });

            case "beginWidgetDrag":
                WidgetDragRequested?.Invoke();
                return new { success = true };

            case "setWidgetTopmost":
            {
                bool topmost = payload.GetProperty("topmost").GetBoolean();
                WidgetTopmostRequested?.Invoke(topmost);
                return new { success = true, topmost };
            }

            case "closeWidget":
                WidgetCloseRequested?.Invoke();
                return new { success = true };

            case "getWidgetsState":
                return _app.Widgets.GetSnapshot();

            case "setWidgetEnabled":
            {
                string type = payload.GetProperty("type").GetString() ?? "";
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return _app.Widgets.SetEnabled(type, enabled);
            }

            case "setWidgetsMaster":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return _app.Widgets.SetMasterEnabled(enabled);
            }

            case "setWidgetPinned":
            {
                string type = payload.GetProperty("type").GetString() ?? "";
                bool pinned = payload.GetProperty("pinned").GetBoolean();
                return _app.Widgets.SetPinned(type, pinned);
            }

            case "setWidgetSize":
            {
                string type = payload.GetProperty("type").GetString() ?? "";
                string size = payload.GetProperty("size").GetString() ?? "medium";
                return _app.Widgets.SetSize(type, size);
            }

            case "setWidgetPlacement":
            {
                string type = payload.GetProperty("type").GetString() ?? "";
                string monitorId = payload.GetProperty("monitorId").GetString() ?? "";
                string anchor = payload.GetProperty("anchor").GetString() ?? "";
                return _app.Widgets.SetPlacement(type, monitorId, anchor);
            }

            case "resetWidgetPosition":
            {
                string type = payload.GetProperty("type").GetString() ?? "";
                return _app.Widgets.ResetPosition(type);
            }

            case "checkDefaultPlans":
            {
                var (allPresent, missing) = await Task.Run(() => _power.CheckDefaultPlans());
                return new { allPresent, missing = missing.Select(m => m.ToString()).ToList() };
            }

            case "restoreDefaultPlans":
                return new { success = await Task.Run(() => _power.RestoreDefaultPlans()) };

            case "getActivePlan":
                return await Task.Run(() => _power.GetActivePlan());

            case "listPowerPlans":
                return await Task.Run(() => _planParams.ListPlans());

            case "getKeepAwakeState":
                return _app.Awake.GetState();

            case "setKeepAwake":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return _app.SetKeepAwake(enabled);
            }

            case "setKeepAwakeSafety":
            {
                var current = _app.Settings.Current.KeepAwake ?? new KeepAwakeSettings();
                bool autoOffBattery = current.AutoDisableOnBattery;
                if (payload.TryGetProperty("autoDisableOnBattery", out var batProp)
                    && (batProp.ValueKind == JsonValueKind.True || batProp.ValueKind == JsonValueKind.False))
                    autoOffBattery = batProp.GetBoolean();
                int maxMinutes = current.MaxMinutes;
                if (payload.TryGetProperty("maxMinutes", out var maxProp) && maxProp.TryGetInt32(out var mm))
                    maxMinutes = mm;
                return await Task.Run(() => _app.Awake.SetSafetyOptions(autoOffBattery, maxMinutes));
            }

            case "getCpuAutomationState":
                return _app.CpuAutomationState;

            case "setActivePlan":
            {
                var planStr = payload.GetProperty("plan").GetString() ?? "";
                if (!Enum.TryParse<PlanId>(planStr, true, out var plan))
                    throw new ArgumentException(_loc.T("Error_UnknownPlan", planStr));
                bool okSet = await Task.Run(() => _power.SetActivePlan(plan));
                return new { success = okSet };
            }

            case "setManualOverride":
            {
                var planStr = payload.GetProperty("plan").GetString() ?? "";
                if (!Enum.TryParse<PlanId>(planStr, true, out var plan))
                    throw new ArgumentException(_loc.T("Error_UnknownPlan", planStr));

                TimeSpan? duration = null;
                if (payload.TryGetProperty("hours", out var hoursEl) &&
                    hoursEl.ValueKind == JsonValueKind.Number)
                {
                    duration = TimeSpan.FromHours(hoursEl.GetDouble());
                }

                bool okOverride = await Task.Run(() => _app.SetManualOverride(plan, duration));
                return new { success = okOverride, @override = _settings.Current.Override };
            }

            case "clearManualOverride":
                await Task.Run(_app.ClearManualOverride);
                return new { success = true, @override = _settings.Current.Override };

            case "getGamingMode":
                return GamingModeStateRequested?.Invoke() ?? new { active = false };

            case "setGamingMode":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                if (GamingModeRequested == null)
                    throw new InvalidOperationException(_loc.T("Error_GamingControlUnavailable"));
                return await GamingModeRequested(enabled);
            }

            case "getSettings":
                return new
                {
                    settings = _settings.Current,
                    startWithWindows = _startup.IsEnabled(),
                    theme = _app.Theme.GetWebTheme(),
                    themeCatalog = _app.Theme.GetWebThemeCatalog(),
                    resolvedLanguage = _loc.CurrentLanguage,
                    resolvedLocale = _loc.CurrentCulture.Name,
                };

            case "setThemeColor":
            {
                string? requested = payload.TryGetProperty("themeColor", out var themeColorElement)
                    ? themeColorElement.GetString()
                    : null;
                AppThemeColorExtensions.TryParse(requested, out var themeColor);
                _settings.Current.ThemeColor = themeColor;
                _settings.Save();
                return _app.Theme.GetWebTheme();
            }

            case "saveSettings":
            {
                var settings = payload.Deserialize<AppSettings>(JsonOpts)
                    ?? throw new ArgumentException(_loc.T("Error_InvalidSettings"));
                // Preserve machine-local/runtime-owned settings: UI never edits them.
                PreserveRuntimeOwnedSettings(settings, _settings.Current);
                _settings.Update(settings);
                _app.RefreshAppPowerProfiles();
                _app.RefreshHeavyAppDetection();
                return new { success = true };
            }

            case "setLanguage":
            {
                string lang = payload.GetProperty("language").GetString() ?? "";
                if (!LanguageResolver.IsSupported(lang))
                    throw new ArgumentException(_loc.T("Error_UnknownMethod", lang));
                var normalized = LanguageResolver.Normalize(lang);
                _settings.Current.Language = normalized;
                _settings.Save();
                _loc.SetLanguage(normalized);
                // Rebuild jump list with new language.
                try { _app.Dispatcher.Invoke(() => _app.SetupJumpListPublic()); } catch { }
                return new { success = true, language = normalized, locale = _loc.CurrentCulture.Name };
            }

            case "setStartWithWindows":
            {
                bool enable = payload.GetProperty("enabled").GetBoolean();
                bool okStart = await Task.Run(() => _startup.SetStartWithWindows(enable));
                _settings.Current.StartWithWindows = enable && okStart;
                _settings.Save();
                return new { success = okStart };
            }

            case "setCloseToTray":
            {
                _settings.Current.CloseToTray = payload.GetProperty("enabled").GetBoolean();
                _settings.Save();
                return new { success = true };
            }

            case "setAutoUpdateChecks":
            {
                bool enable = payload.GetProperty("enabled").GetBoolean();
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.Enabled = enable;
                if (enable)
                    _settings.Current.AutoUpdates.SnoozedUntilUtc = null;
                _settings.Save();
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "setSilentAutoUpdates":
            {
                bool enable = payload.GetProperty("enabled").GetBoolean();
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.SilentInstallEnabled = enable;
                _settings.Save();
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "setUpdateChannel":
            {
                string channel = payload.GetProperty("channel").GetString() ?? "stable";
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.UpdateChannel = channel;
                _settings.Save();
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "setPreviewUpdates":
            {
                bool enable = payload.GetProperty("enabled").GetBoolean();
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.PreviewChannel = enable;
                _settings.Save();
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "snoozeUpdate":
            {
                int minutes = payload.TryGetProperty("minutes", out var minutesEl) && minutesEl.ValueKind == JsonValueKind.Number
                    ? minutesEl.GetInt32()
                    : 30;
                minutes = UpdateSchedulePolicy.NormalizeSnoozeMinutes(minutes);
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.SnoozedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                _settings.Save();
                return new { success = true, snoozedUntilUtc = _settings.Current.AutoUpdates.SnoozedUntilUtc };
            }

            case "skipUpdateVersion":
            {
                string version = payload.GetProperty("version").GetString() ?? "";
                version = version.Trim().TrimStart('v', 'V');
                if (version.Length == 0)
                    throw new ArgumentException(_loc.T("Error_MissingUpdateVersion"));
                _settings.Current.AutoUpdates ??= new AutoUpdateSettings();
                _settings.Current.AutoUpdates.SkippedVersion = version;
                _settings.Current.AutoUpdates.SnoozedUntilUtc = null;
                _settings.Save();
                return new { success = true, skippedVersion = version };
            }

            case "getHeavyAppStatus":
                return await Task.Run(_app.GetHeavyAppStatus);

            case "refreshHeavyAppDetection":
                return await Task.Run(_app.RefreshHeavyAppDetection);

            case "getAppPowerProfileStatus":
                return await Task.Run(_app.GetAppPowerProfileStatus);

            case "refreshAppPowerProfiles":
                return await Task.Run(_app.RefreshAppPowerProfiles);

            case "getPowerSourcePlanState":
                return await Task.Run(() => _app.GetPowerSourcePlanState());

            case "setPowerSourcePlanSwitch":
            {
                bool enable = payload.GetProperty("enabled").GetBoolean();
                return await Task.Run(() => _app.SetPowerSourcePlanSwitch(enable));
            }

            case "getThermalGuardState":
                return await Task.Run(() => _app.ThermalGuard.Current);

            case "setThermalGuardEnabled":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return await Task.Run(() => _app.ThermalGuard.SetEnabled(enabled));
            }

            case "setThermalGuardSettings":
            {
                var raw = payload.Deserialize<ThermalGuardSettings>(JsonOpts)
                    ?? throw new ArgumentException("Invalid thermal guard settings");
                return await Task.Run(() => _app.ThermalGuard.ApplySettings(raw));
            }

            case "getIdlePowerGuardState":
                return await Task.Run(() => _app.IdlePowerGuard.Current);

            case "setIdlePowerGuardEnabled":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return await Task.Run(() => _app.IdlePowerGuard.SetEnabled(enabled));
            }

            case "setIdlePowerGuardSettings":
            {
                var raw = payload.Deserialize<IdlePowerGuardSettings>(JsonOpts)
                    ?? throw new ArgumentException("Invalid idle power guard settings");
                return await Task.Run(() => _app.IdlePowerGuard.ApplySettings(raw));
            }

            case "pickAppPowerProfileExecutable":
            {
                string? path = await _webView.Dispatcher.InvokeAsync(PickAppPowerProfileExecutable);
                return new { path };
            }

            case "getTopProcesses":
            {
                int count = payload.ValueKind != JsonValueKind.Undefined &&
                            payload.TryGetProperty("count", out var cntEl) &&
                            cntEl.ValueKind == JsonValueKind.Number
                    ? cntEl.GetInt32() : 8;
                return await Task.Run(() => _monitor.GetTopProcesses(count));
            }

            case "getStartupApps":
                return await Task.Run(() => _startupApps.GetStartupApps());

            case "pickStartupExecutable":
            {
                string? path = await _webView.Dispatcher.InvokeAsync(() => _startupApps.PickExecutablePath());
                return new { path };
            }

            case "addStartupApp":
            {
                string path = payload.GetProperty("path").GetString()
                    ?? throw new ArgumentException(_loc.T("Error_MissingPath"));
                var entry = await Task.Run(() => _startupApps.AddManagedStartupApp(path));
                return new { success = true, entry };
            }

            case "setStartupAppEnabled":
            {
                string id = payload.GetProperty("id").GetString()
                    ?? throw new ArgumentException(_loc.T("Error_MissingId"));
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                bool changed = await Task.Run(() => _startupApps.SetStartupAppEnabled(id, enabled));
                return new { success = changed };
            }

            case "removeStartupApp":
            {
                string id = payload.GetProperty("id").GetString()
                    ?? throw new ArgumentException(_loc.T("Error_MissingId"));
                bool removed = await Task.Run(() => _startupApps.RemoveManagedStartupApp(id));
                return new { success = removed };
            }

            case "checkForUpdates":
                return await _updates.CheckForUpdatesAsync();

            case "getReleaseHistory":
                return await _updates.GetReleaseHistoryAsync();

            case "downloadUpdate":
            {
                var url = payload.GetProperty("url").GetString()
                    ?? throw new ArgumentException("URL mancante");

                // Do not restart while a game is running — queue and install after the session.
                if (_app.IsHeavyAppSessionActive())
                {
                    _app.DeferUpdateUntilGameEnds(url);
                    return new
                    {
                        success = false,
                        deferred = true,
                        reason = "heavyAppActive",
                        message = _loc.T("Dialog_UpdateDeferredGame"),
                    };
                }

                string path = await _updates.DownloadUpdateAsync(url);
                if (_app.IsHeavyAppSessionActive())
                {
                    _app.DeferUpdateUntilGameEnds(url);
                    return new
                    {
                        success = false,
                        deferred = true,
                        reason = "heavyAppActive",
                        message = _loc.T("Dialog_UpdateDeferredGame"),
                    };
                }

                Process.Start(new ProcessStartInfo(path,
                    $"/update --pid {Environment.ProcessId} --lang {_loc.CurrentLanguage}") { UseShellExecute = true });
                ExitRequested?.Invoke();
                return new { success = true };
            }

            case "logError":
            {
                string message = payload.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? "" : "";
                string? stack = payload.TryGetProperty("stack", out var stEl)
                    ? stEl.GetString() : null;
                return BridgeRpc.HandleLogError(message, stack, Logger.Error);
            }

            case "openExternal":
            {
                var url = payload.GetProperty("url").GetString() ?? "";
                if (url.StartsWith("https://") || url.StartsWith("http://"))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return new { success = true };
            }

            case "exitApp":
                ExitRequested?.Invoke();
                return new { success = true };

            case "minimizeToTray":
                MinimizeToTrayRequested?.Invoke();
                return new { success = true };

            case "getScheduledPowerAction":
                return _app.ScheduledPowerActions.GetState();

            case "schedulePowerAction":
            {
                string modeText = payload.GetProperty("mode").GetString() ?? "";
                string actionText = payload.GetProperty("action").GetString() ?? "";

                if (!Enum.TryParse<ScheduledPowerActionType>(actionText, ignoreCase: true, out var action))
                    throw new ArgumentException(_loc.T("Error_InvalidPowerAction"));

                if (string.Equals(modeText, "relative", StringComparison.OrdinalIgnoreCase))
                {
                    int delayMinutes = payload.GetProperty("delayMinutes").GetInt32();
                    return _app.ScheduledPowerActions.ScheduleAfter(TimeSpan.FromMinutes(delayMinutes), action);
                }

                if (string.Equals(modeText, "daily", StringComparison.OrdinalIgnoreCase))
                {
                    string timeText = payload.GetProperty("time").GetString() ?? "";
                    if (!TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                        throw new ArgumentException(_loc.T("Error_InvalidPowerTime"));
                    return _app.ScheduledPowerActions.ScheduleDaily(time, action);
                }

                throw new ArgumentException(_loc.T("Error_InvalidScheduleMode"));
            }

            case "cancelScheduledPowerAction":
                return _app.ScheduledPowerActions.Cancel();

            case "getPlanTimeouts":
            {
                string? guid = payload.TryGetProperty("planGuid", out var guidEl)
                    ? guidEl.GetString() : null;
                return await Task.Run(() => _planParams.GetPlanTimeouts(guid));
            }

            case "getPlanParameters":
            {
                string? guid = payload.TryGetProperty("planGuid", out var guidEl)
                    ? guidEl.GetString() : null;
                return await Task.Run(() => _planParams.GetPlanParameters(guid));
            }

            case "setPlanParameter":
            {
                string planGuid = payload.GetProperty("planGuid").GetString()
                    ?? throw new ArgumentException(_loc.T("Error_MissingPlanGuid"));
                string settingKey = payload.GetProperty("settingKey").GetString()
                    ?? throw new ArgumentException(_loc.T("Error_MissingSettingKey"));
                int acValue = payload.GetProperty("acValue").GetInt32();
                int dcValue = payload.GetProperty("dcValue").GetInt32();
                bool ok = await Task.Run(() => _planParams.SetPlanParameter(planGuid, settingKey, acValue, dcValue));
                return new { success = ok };
            }

            case "getMemoryStatus":
                return await Task.Run(() => _memoryOptimizer.GetMemoryStatus());

            case "purgeStandbyList":
            {
                bool purged = await Task.Run(() => _app.StandbyAutoCleaner.PurgeManual());
                var status = await Task.Run(() => _memoryOptimizer.GetMemoryStatus());
                return new { success = purged, memory = status };
            }

            case "getStandbyAutoCleanSettings":
                return _settings.Current.StandbyAutoCleaner;

            case "setStandbyAutoCleanSettings":
            {
                var autoSettings = payload.Deserialize<StandbyAutoCleanerSettings>(JsonOpts)
                    ?? throw new ArgumentException("Impostazioni StandbyAutoCleaner non valide");
                var savedSettings = SaveStandbyAutoCleanSettings(_settings, autoSettings);
                return new { success = true, settings = savedSettings };
            }

            case "exportSettings":
            {
                string? path = await _webView.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = _loc.T("FilePicker_ExportTitle"),
                        Filter = _loc.T("FilePicker_JsonFilter"),
                        FileName = $"voltmanager-settings-{DateTime.Now:yyyyMMdd}.json",
                    };
                    return dialog.ShowDialog() == true ? dialog.FileName : null;
                });
                if (path == null) return new { success = false, cancelled = true };
                await Task.Run(() => System.IO.File.WriteAllText(path,
                    JsonSerializer.Serialize(_settings.Current, BackupJsonOpts)));
                return new { success = true, path };
            }

            case "openLogFolder":
            {
                bool ok = DiagnosticsReportService.TryOpenLogFolder(out var logDir, out var err);
                return new { success = ok, path = logDir, error = err };
            }

            case "exportDiagnostics":
            {
                string? path = await _webView.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = _loc.T("FilePicker_DiagnosticsTitle"),
                        Filter = _loc.T("FilePicker_TextFilter"),
                        FileName = $"voltmanager-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                    };
                    return dialog.ShowDialog() == true ? dialog.FileName : null;
                });
                if (path == null) return new { success = false, cancelled = true };

                var report = await Task.Run(() =>
                {
                    var plan = _power.GetActivePlan();
                    var planSummary = plan == null
                        ? "(none)"
                        : $"{plan.PlanId?.ToString() ?? "?"}  {plan.Name}  [{plan.Guid}]  active={plan.IsActive}";

                    BatteryPowerState? bat = null;
                    try
                    {
                        var raw = _powerFlow.GetState();
                        IReadOnlyList<BatteryHistorySample>? hist = null;
                        try { hist = _app.BatteryHistory.GetHistory(); } catch { }
                        bat = _batteryPowerSmoother.Apply(raw, hist, DateTime.UtcNow);
                    }
                    catch { /* optional */ }

                    var snap = new DiagnosticsSnapshot
                    {
                        AppVersion = DiagnosticsReportService.ResolveAppVersion(),
                        LogPath = DiagnosticsReportService.LogFilePath,
                        SystemInfo = _hardware.GetSystemInfo(),
                        Metrics = _monitor.Latest,
                        ActivePlanSummary = planSummary,
                        KeepAwake = _app.Awake.GetState(),
                        PowerSource = _app.PowerSourcePlans.Current,
                        ThermalGuard = _app.ThermalGuard.Current,
                        IdlePowerGuard = _app.IdlePowerGuard.Current,
                        CpuAutomation = _app.CpuAutomationState,
                        BatteryPower = bat,
                        BatteryHealth = _batteryHealth.GetHealth(),
                        Memory = _memoryOptimizer.GetMemoryStatus(),
                        SettingsJson = DiagnosticsReportService.SanitizeSettingsJson(_settings.Current),
                    };
                    return DiagnosticsReportService.BuildReport(snap);
                });

                await Task.Run(() => System.IO.File.WriteAllText(path, report));
                return new { success = true, path, bytes = report.Length };
            }

            case "importSettings":
            {
                string? path = await _webView.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = _loc.T("FilePicker_ImportTitle"),
                        Filter = _loc.T("FilePicker_JsonFilter"),
                        CheckFileExists = true,
                    };
                    return dialog.ShowDialog() == true ? dialog.FileName : null;
                });
                if (path == null) return new { success = false, cancelled = true };
                var json = await Task.Run(() => System.IO.File.ReadAllText(path));
                var imported = JsonSerializer.Deserialize<AppSettings>(json, BackupJsonOpts)
                    ?? throw new ArgumentException(_loc.T("Error_InvalidBackupFile"));
                // Machine-local/runtime state must survive an import from another PC.
                PreserveRuntimeOwnedSettings(imported, _settings.Current);
                _settings.Update(imported);
                _app.RefreshAppPowerProfiles();
                _app.RefreshHeavyAppDetection();
                return new { success = true, path };
            }

            default:
                throw new ArgumentException(_loc.T("Error_UnknownMethod", method));
        }
    }

    internal static void PreserveRuntimeOwnedSettings(AppSettings settings, AppSettings current)
    {
        settings.PlanGuidMap = current.PlanGuidMap;
        settings.Override = current.Override;
        // Host-owned: UI payloads do not carry task-schema migration state.
        settings.AutostartTaskSchemaVersion = current.AutostartTaskSchemaVersion;
        settings.StandbyAutoCleaner = current.StandbyAutoCleaner;
        settings.AutoShutdown ??= new AutoShutdownSettings();
        settings.AutoUpdates ??= new AutoUpdateSettings();
        settings.HeavyAppDetection ??= new HeavyAppDetectionSettings();
        settings.AppPowerProfiles ??= new AppPowerProfileSettings();
        settings.CpuAutomation ??= new CpuAutomationSettings();
        current.AutoShutdown ??= new AutoShutdownSettings();
        current.AutoUpdates ??= new AutoUpdateSettings();
        current.Widgets ??= new WidgetSettings();
        current.Widgets.Normalize();
        settings.Widgets = current.Widgets;
        settings.AutoShutdown = current.AutoShutdown;
        settings.AutoUpdates.SnoozedUntilUtc = current.AutoUpdates.SnoozedUntilUtc;
        settings.AutoUpdates.SkippedVersion = current.AutoUpdates.SkippedVersion;
        // UI saveSettings often omits language; never wipe a previously chosen locale.
        if (string.IsNullOrWhiteSpace(settings.Language) || !LanguageResolver.IsSupported(settings.Language))
            settings.Language = current.Language ?? "";
        else
            settings.Language = LanguageResolver.Normalize(settings.Language);
    }

    internal static StandbyAutoCleanerSettings SaveStandbyAutoCleanSettings(
        SettingsService settingsService,
        StandbyAutoCleanerSettings autoSettings)
    {
        settingsService.Current.StandbyAutoCleaner = autoSettings;
        settingsService.Save();
        return settingsService.Current.StandbyAutoCleaner;
    }

    private string? PickAppPowerProfileExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = _loc.T("FilePicker_AppProfileTitle"),
            Filter = _loc.T("FilePicker_AppProfileFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
