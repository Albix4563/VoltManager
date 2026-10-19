using System;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using VoltManager.Services;

namespace VoltManager;

public partial class MainWindow
{
    private int _updateReadyCheckActive;

    /// <summary>
    /// A newly installed app acknowledges exactly its own update session after
    /// the expected document has loaded and its frontend/bridge bootstrap works.
    /// Core initialization or a successful about:blank navigation is insufficient.
    /// </summary>
    private async void NotifyUpdateNavigationCompleted(
        CoreWebView2 core,
        CoreWebView2NavigationCompletedEventArgs navigation)
    {
        if (!_justUpdated || !navigation.IsSuccess || _dashboardLoadFailed ||
            _startMinimized || !IsVisible || !ReferenceEquals(WebView.CoreWebView2, core))
            return;

        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.FindIndex(arguments,
            value => string.Equals(value, UpdateSessionProtocol.SessionArgument,
                StringComparison.OrdinalIgnoreCase));
        if (index < 1 || index + 1 >= arguments.Length ||
            !UpdateSessionProtocol.TryValidateId(arguments[index + 1], out string sessionId) ||
            !IsUpdateDashboardDocument(core.Source))
            return;

        if (Interlocked.CompareExchange(ref _updateReadyCheckActive, 1, 0) != 0)
            return;
        try
        {
            // The same criteria are used by the Windows startup validation probe:
            // actual app content, settings, an active host bridge and a response to
            // the real getActivePlan RPC. The probe itself is validation-only, so
            // this narrow check is needed for production update handoff.
            const string script = """
                (async () => {
                    const deadline = Date.now() + 25000;
                    while (Date.now() < deadline) {
                        const settings = window.__voltSettings?.get?.();
                        const overview = document.querySelector('#view-overview.vm-reorg-view:not(.hidden)');
                        const cpu = document.querySelector('#cpu-name');
                        const info = window.VoltSystemInfo;
                        if (document.readyState !== 'loading' && settings && info &&
                            cpu && info.cpuName && cpu.textContent === info.cpuName &&
                            overview && getComputedStyle(overview).display !== 'none' &&
                            window.Host?.available && typeof Host.call === 'function' &&
                            document.visibilityState === 'visible') {
                            try {
                                const plan = await Host.call('getActivePlan');
                                if (plan && plan.planId) {
                                    await new Promise(resolve => requestAnimationFrame(
                                        () => requestAnimationFrame(resolve)));
                                    return true;
                                }
                            } catch (_) { /* bridge may not be ready yet */ }
                        }
                        await new Promise(resolve => setTimeout(resolve, 150));
                    }
                    return false;
                })();
                """;
            string result = await core.ExecuteScriptAsync(script);
            if (result == "true" && !_dashboardLoadFailed && IsVisible &&
                ReferenceEquals(WebView.CoreWebView2, core) &&
                IsUpdateDashboardDocument(core.Source))
            {
                if (UpdateSessionProtocol.TrySignalReady(sessionId))
                    Logger.Info("Update session dashboard readiness acknowledged.");
                else
                    Logger.Warn("Update session readiness event is unavailable.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Update session dashboard readiness check failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _updateReadyCheckActive, 0);
        }
    }

    private static bool IsUpdateDashboardDocument(string? source)
        => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "app.local", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.AbsolutePath, "/index.html", StringComparison.OrdinalIgnoreCase);
}
