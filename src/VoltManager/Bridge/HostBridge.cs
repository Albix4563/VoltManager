using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
    private readonly MonitorService _monitor;
    private readonly App _app;

    public event Action? ExitRequested;
    public event Action? MinimizeToTrayRequested;
    public event Func<bool, Task<object?>>? GamingModeRequested;
    public event Func<object?>? GamingModeStateRequested;

    public HostBridge(WebView2 webView, HardwareInfoService hardware, PowerPlanService power,
        SettingsService settings, UpdateService updates, StartupService startup, MonitorService monitor, App app)
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
        _updates.DownloadProgress += pct => PushEvent("updateDownloadProgress", new { pct });
        _app.HeavyApps.ActivityChanged += state => PushEvent("heavyAppActivityChanged", state);
        _app.AppProfiles.ActivityChanged += state => PushEvent("appPowerProfileActivityChanged", state);
        _app.StandbyAutoCleaner.AutoCleaned += freshMem => PushEvent("standbyAutoCleaned", freshMem);
    }

    public void PushEvent(string name, object data)
    {
        var payload = JsonSerializer.Serialize(new { @event = name, data }, JsonOpts);
        _webView.Dispatcher.Invoke(() =>
        {
            try { _webView.CoreWebView2?.PostWebMessageAsJson(payload); }
            catch { }
        });
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
            Reply(id!, ok: true, result);
        }
        catch (Exception ex)
        {
            Logger.Error("Bridge message handling failed (id: " + (id ?? "none") + ")", ex);
            if (id != null) Reply(id, ok: false, ex.Message);
        }
    }

    private void Reply(string id, bool ok, object? result)
    {
        var msg = ok
            ? JsonSerializer.Serialize(new { id, ok = true, result }, JsonOpts)
            : JsonSerializer.Serialize(new { id, ok = false, error = result?.ToString() ?? "errore" }, JsonOpts);
        _webView.Dispatcher.Invoke(() =>
        {
            try { _webView.CoreWebView2?.PostWebMessageAsJson(msg); }
            catch { }
        });
    }

    private async Task<object?> DispatchAsync(string method, JsonElement payload)
    {
        switch (method)
        {
            case "getSystemInfo":
                return _hardware.GetSystemInfo();

            case "getBatteryHealth":
                return await Task.Run(() => _batteryHealth.GetHealth());

            case "getBatteryPower":
                return await Task.Run(() => _powerFlow.GetState());

            case "getBatteryHistory":
                return await Task.Run(() => new { samples = _app.BatteryHistory.GetHistory() });

            case "checkDefaultPlans":
            {
                var (allPresent, missing) = await Task.Run(() => _power.CheckDefaultPlans());
                return new { allPresent, missing = missing.Select(m => m.ToString()).ToList() };
            }

            case "restoreDefaultPlans":
                return new { success = await Task.Run(() => _power.RestoreDefaultPlans()) };

            case "getActivePlan":
                return await Task.Run(() => _power.GetActivePlan());

            case "setActivePlan":
            {
                var planStr = payload.GetProperty("plan").GetString() ?? "";
                if (!Enum.TryParse<PlanId>(planStr, true, out var plan))
                    throw new ArgumentException($"Piano sconosciuto: {planStr}");
                bool okSet = await Task.Run(() => _power.SetActivePlan(plan));
                return new { success = okSet };
            }

            case "setManualOverride":
            {
                var planStr = payload.GetProperty("plan").GetString() ?? "";
                if (!Enum.TryParse<PlanId>(planStr, true, out var plan))
                    throw new ArgumentException($"Piano sconosciuto: {planStr}");

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
                    throw new InvalidOperationException("Controllo modalità gaming non disponibile");
                return await GamingModeRequested(enabled);
            }

            case "getSettings":
                return new
                {
                    settings = _settings.Current,
                    startWithWindows = _startup.IsEnabled(),
                };

            case "saveSettings":
            {
                var settings = payload.Deserialize<AppSettings>(JsonOpts)
                    ?? throw new ArgumentException("Impostazioni non valide");
                // Preserve machine-local/runtime-owned settings: UI never edits them.
                PreserveRuntimeOwnedSettings(settings, _settings.Current);
                _settings.Update(settings);
                _app.RefreshAppPowerProfiles();
                _app.RefreshHeavyAppDetection();
                return new { success = true };
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
                minutes = Math.Clamp(minutes, 5, 1440);
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
                    throw new ArgumentException("Versione aggiornamento mancante");
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
                    ?? throw new ArgumentException("Percorso mancante");
                var entry = await Task.Run(() => _startupApps.AddManagedStartupApp(path));
                return new { success = true, entry };
            }

            case "setStartupAppEnabled":
            {
                string id = payload.GetProperty("id").GetString()
                    ?? throw new ArgumentException("ID mancante");
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                bool changed = await Task.Run(() => _startupApps.SetStartupAppEnabled(id, enabled));
                return new { success = changed };
            }

            case "removeStartupApp":
            {
                string id = payload.GetProperty("id").GetString()
                    ?? throw new ArgumentException("ID mancante");
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
                string path = await _updates.DownloadUpdateAsync(url);
                Process.Start(new ProcessStartInfo(path,
                    $"/update --pid {Environment.ProcessId}") { UseShellExecute = true });
                ExitRequested?.Invoke();
                return new { success = true };
            }

            case "logError":
            {
                string message = payload.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? "" : "";
                string? stack = payload.TryGetProperty("stack", out var stEl)
                    ? stEl.GetString() : null;
                Logger.Error("[UI] " + message + (stack != null ? "\n" + stack : ""));
                return new { success = true };
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

            case "getPlanParameters":
            {
                string? guid = payload.TryGetProperty("planGuid", out var guidEl)
                    ? guidEl.GetString() : null;
                return await Task.Run(() => _planParams.GetPlanParameters(guid));
            }

            case "setPlanParameter":
            {
                string planGuid = payload.GetProperty("planGuid").GetString()
                    ?? throw new ArgumentException("planGuid mancante");
                string settingKey = payload.GetProperty("settingKey").GetString()
                    ?? throw new ArgumentException("settingKey mancante");
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

            default:
                throw new ArgumentException($"Metodo sconosciuto: {method}");
        }
    }

    internal static void PreserveRuntimeOwnedSettings(AppSettings settings, AppSettings current)
    {
        settings.PlanGuidMap = current.PlanGuidMap;
        settings.Override = current.Override;
        settings.StandbyAutoCleaner = current.StandbyAutoCleaner;
        settings.AutoShutdown ??= new AutoShutdownSettings();
        settings.AutoUpdates ??= new AutoUpdateSettings();
        settings.HeavyAppDetection ??= new HeavyAppDetectionSettings();
        settings.AppPowerProfiles ??= new AppPowerProfileSettings();
        current.AutoShutdown ??= new AutoShutdownSettings();
        current.AutoUpdates ??= new AutoUpdateSettings();
        settings.AutoShutdown.LastTriggeredLocalDate = current.AutoShutdown.LastTriggeredLocalDate;
        settings.AutoUpdates.SnoozedUntilUtc = current.AutoUpdates.SnoozedUntilUtc;
        settings.AutoUpdates.SkippedVersion = current.AutoUpdates.SkippedVersion;
    }

    internal static StandbyAutoCleanerSettings SaveStandbyAutoCleanSettings(
        SettingsService settingsService,
        StandbyAutoCleanerSettings autoSettings)
    {
        settingsService.Current.StandbyAutoCleaner = autoSettings;
        settingsService.Save();
        return settingsService.Current.StandbyAutoCleaner;
    }

    private static string? PickAppPowerProfileExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona applicazione per profilo energetico",
            Filter = "Applicazioni (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
