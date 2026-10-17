using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Windows;
using VoltManager.Services;

namespace VoltManager;

public partial class MainWindow
{
#if VOLTMANAGER_VALIDATION_HOOKS
    private CoreWebView2? _startupProbeCore;
    private bool _startupFrameObserved;
    private bool _startupReadyObserved;

    private void AttachStartupProbe(CoreWebView2 core)
    {
        if (!ValidationEnvironment.IsActive) return;
        if (_startupProbeCore != null)
        {
            _startupProbeCore.WebMessageReceived -= OnStartupProbeMessage;
            if (!ReferenceEquals(_startupProbeCore, core) && !_startupReadyObserved)
                StartupValidationProbe.Mark("webview_recovery_during_startup");
        }
        _startupFrameObserved = false;
        _startupProbeCore = core;
        core.WebMessageReceived += OnStartupProbeMessage;
    }

    private void OnStartupProbeMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (sender != _startupProbeCore || _startMinimized || _dashboardLoadFailed ||
            !IsVisible || WebView.Visibility != Visibility.Visible ||
            !e.Source.StartsWith("https://app.local/index.html", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("__voltStartupProbe", out JsonElement value)) return;
            if (value.GetString() == "first-frame" && !_startupFrameObserved)
            {
                _startupFrameObserved = true;
                StartupValidationProbe.Mark("first_frame");
            }
            if (value.GetString() == "dashboard-ready" && _startupFrameObserved && !_startupReadyObserved)
            {
                _startupReadyObserved = true;
                StartupValidationProbe.Mark("dashboard_ready");
            }
        }
        catch (JsonException) { /* Ignore malformed validation-only messages. */ }
        catch (InvalidOperationException) { /* Ignore messages without the expected validation payload. */ }
    }

    private async void StartStartupProbe(CoreWebView2 core)
    {
        if (!ValidationEnvironment.IsActive || _startMinimized || _startupReadyObserved) return;
        // Injected only in validation: observes real initial payloads, the first painted
        // metric and a successful round trip over the existing bridge RPC.
        const string script = """
            (() => {
                if (window.__voltStartupProbeInstalled) return;
                window.__voltStartupProbeInstalled = true;
                let framed = false, metric = false, bridge = false, failed = false;
                const send = value => chrome.webview.postMessage({__voltStartupProbe: value});
                requestAnimationFrame(() => requestAnimationFrame(() => {
                    framed = true;
                    send('first-frame');
                }));
                if (!window.Host || !Host.available || typeof Host.on !== 'function') return;
                Host.on('metrics', m => {
                    if (m && Number.isFinite(Number(m.cpu)) && Number.isFinite(Number(m.ramPct))) metric = true;
                });
                Host.call('getActivePlan').then(plan => {
                    bridge = !!plan && !!plan.planId;
                }).catch(() => { failed = true; });
                const observer = setInterval(() => {
                    const s = window.__voltSettings?.get?.();
                    const info = window.VoltSystemInfo;
                    const cpu = document.querySelector('#cpu-name');
                    const overview = document.querySelector('#view-overview.vm-reorg-view:not(.hidden)');
                    const cpuValue = overview?.querySelector('#ov-metric-cpu');
                    const ramValue = overview?.querySelector('#ov-metric-ram');
                    const activePlan = overview?.querySelector('#ov-status-plan');
                    const systemRendered = !!info && !!cpu && cpu.textContent === info.cpuName && !!info.cpuName;
                    const overviewVisible = !!overview && getComputedStyle(overview).display !== 'none';
                    const metricRendered = !!cpuValue && /\d+(?:\.\d+)?%/.test(cpuValue.textContent || '') &&
                        !!ramValue && /\d+(?:\.\d+)?%/.test(ramValue.textContent || '');
                    const planRendered = !!activePlan && !!activePlan.textContent?.trim() &&
                        activePlan.textContent.trim() !== '--';
                    if (!failed && framed && metric && bridge && s && systemRendered && overviewVisible &&
                        metricRendered && planRendered &&
                        document.visibilityState === 'visible') {
                        clearInterval(observer);
                        send('dashboard-ready');
                    }
                }, 100);
                setTimeout(() => clearInterval(observer), 18000);
            })();
            """;
        try { await core.ExecuteScriptAsync(script); }
        catch (Exception ex) { Logger.Warn("Startup validation probe failed: " + ex.Message); }
    }
#else
    private static void AttachStartupProbe(CoreWebView2 core) { }
    private static void StartStartupProbe(CoreWebView2 core) { }
#endif
}
