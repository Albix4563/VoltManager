using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly App _app;

    public event Action? ExitRequested;
    public event Action? MinimizeToTrayRequested;

    public HostBridge(WebView2 webView, HardwareInfoService hardware, PowerPlanService power,
        SettingsService settings, UpdateService updates, StartupService startup, App app)
    {
        _webView = webView;
        _hardware = hardware;
        _power = power;
        _settings = settings;
        _updates = updates;
        _startup = startup;
        _app = app;
    }

    public void Attach()
    {
        _webView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            string json = e.WebMessageAsJson;
            await HandleMessageAsync(json);
        };
        _updates.DownloadProgress += pct => PushEvent("updateDownloadProgress", new { pct });
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
                settings.PlanGuidMap = _settings.Current.PlanGuidMap;
                settings.Override = _settings.Current.Override;
                settings.AutoShutdown ??= new AutoShutdownSettings();
                settings.AutoShutdown.LastTriggeredLocalDate = _settings.Current.AutoShutdown.LastTriggeredLocalDate;
                _settings.Update(settings);
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

            case "checkForUpdates":
                return await _updates.CheckForUpdatesAsync();

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

            default:
                throw new ArgumentException($"Metodo sconosciuto: {method}");
        }
    }
}
