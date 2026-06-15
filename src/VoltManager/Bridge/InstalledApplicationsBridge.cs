using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using VoltManager.Services;

namespace VoltManager.Bridge;

public static class InstalledApplicationsBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Attach(WebView2 webView)
    {
        var service = new InstalledApplicationsService();

        webView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            string? id = null;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                id = root.GetProperty("id").GetString();
                string method = root.GetProperty("method").GetString() ?? "";

                if (method == "getInstalledApplications")
                {
                    Reply(webView, id!, true, await Task.Run(service.GetInstalledApplications));
                    return;
                }

                if (method == "startInstalledAppRemoval")
                {
                    JsonElement payload = root.TryGetProperty("payload", out var p) ? p : default;
                    string appId = payload.GetProperty("id").GetString()
                        ?? throw new ArgumentException("ID applicazione mancante");
                    bool quiet = payload.TryGetProperty("preferQuiet", out var q) &&
                                 (q.ValueKind == JsonValueKind.True || q.ValueKind == JsonValueKind.False) &&
                                 q.GetBoolean();

                    Reply(webView, id!, true, await Task.Run(() => service.UninstallApplication(appId, quiet)));
                    return;
                }

                if (method == "openWindowsAppsSettings")
                {
                    Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
                    Reply(webView, id!, true, new { success = true });
                }
            }
            catch (Exception ex)
            {
                if (id != null) Reply(webView, id, false, ex.Message);
            }
        };
    }

    private static void Reply(WebView2 webView, string id, bool ok, object? result)
    {
        var message = ok
            ? JsonSerializer.Serialize(new { id, ok = true, result }, JsonOpts)
            : JsonSerializer.Serialize(new { id, ok = false, error = result?.ToString() ?? "errore" }, JsonOpts);

        webView.Dispatcher.Invoke(() =>
        {
            try { webView.CoreWebView2?.PostWebMessageAsJson(message); }
            catch { }
        });
    }
}
