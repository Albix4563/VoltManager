using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager.Bridge;

/// <summary>
/// JSON-RPC transport over WebView2. Domain routing is delegated to BridgeRpcDispatcher.
/// </summary>
public class HostBridge : IDisposable
{
    private readonly WebView2 _webView;
    private readonly UpdateService _updates;
    private readonly PowerPlanService _power;
    private readonly App _app;
    private readonly LocalizationService _loc;
    private readonly bool _subscribeGlobalEvents;
    private readonly BridgeLifetime _lifetime = new();
    private readonly BridgeRpcDispatcher _dispatcher;
    private readonly UiMetricsPublisher _metricsPublisher = new();
    private volatile bool _disposed;
    private bool _pushEventFaulted;

    public event Action? ExitRequested;
    public event Action? MinimizeToTrayRequested;
    public event Func<bool, Task<object?>>? GamingModeRequested;
    public event Func<object?>? GamingModeStateRequested;
    public event Action? WidgetDragRequested;
    public event Action<bool>? WidgetTopmostRequested;
    public event Action? WidgetCloseRequested;

    public HostBridge(
        WebView2 webView,
        HardwareInfoService hardware,
        PowerPlanService power,
        SettingsService settings,
        UpdateService updates,
        StartupService startup,
        MonitorService monitor,
        App app,
        bool subscribeGlobalEvents = true)
    {
        _webView = webView;
        _updates = updates;
        _power = power;
        _app = app;
        _loc = app.Loc;
        _subscribeGlobalEvents = subscribeGlobalEvents;

        var dialogs = new WebViewBridgeFileDialogService(webView);
        _dispatcher = new BridgeRpcDispatcher(
            BridgeHandlerFactory.Create(
                hardware,
                power,
                settings,
                updates,
                startup,
                monitor,
                app,
                _loc,
                dialogs,
                () => GamingModeStateRequested?.Invoke() ?? new { active = false },
                HandleGamingModeRequestedAsync,
                () => ExitRequested?.Invoke(),
                () => MinimizeToTrayRequested?.Invoke(),
                () => WidgetDragRequested?.Invoke(),
                topmost => WidgetTopmostRequested?.Invoke(topmost),
                () => WidgetCloseRequested?.Invoke()),
            method => _loc.T("Error_UnknownMethod", method));
    }

    public void Attach()
    {
        if (_disposed)
            return;

        CoreWebView2? core = _webView.CoreWebView2;
        if (core == null || !_lifetime.TryBeginAttach())
            return;

        core.WebMessageReceived += OnWebMessageReceived;
        _lifetime.RegisterDetach(() =>
        {
            try { core.WebMessageReceived -= OnWebMessageReceived; }
            catch { }
        });

        if (!_subscribeGlobalEvents)
            return;

        _updates.DownloadProgress += OnDownloadProgress;
        _lifetime.RegisterDetach(() => _updates.DownloadProgress -= OnDownloadProgress);

        _app.HeavyApps.ActivityChanged += OnHeavyAppsChanged;
        _lifetime.RegisterDetach(() => _app.HeavyApps.ActivityChanged -= OnHeavyAppsChanged);

        _app.AppProfiles.ActivityChanged += OnAppProfilesChanged;
        _lifetime.RegisterDetach(() => _app.AppProfiles.ActivityChanged -= OnAppProfilesChanged);

        _app.ActivePlanReasonChanged += OnPlanReasonChanged;
        _lifetime.RegisterDetach(() => _app.ActivePlanReasonChanged -= OnPlanReasonChanged);

        _app.PowerPlanConflictDetected += OnPlanConflict;
        _lifetime.RegisterDetach(() => _app.PowerPlanConflictDetected -= OnPlanConflict);

        _power.History.Changed += OnPlanHistoryChanged;
        _lifetime.RegisterDetach(() => _power.History.Changed -= OnPlanHistoryChanged);

        _app.StandbyAutoCleaner.AutoCleaned += OnStandbyCleaned;
        _lifetime.RegisterDetach(() => _app.StandbyAutoCleaner.AutoCleaned -= OnStandbyCleaned);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsStopped)
            return;

        string json;
        try { json = e.WebMessageAsJson; }
        catch (Exception ex)
        {
            Logger.Error("Could not read web message", ex);
            return;
        }

        await HandleMessageAsync(json);
    }

    private void OnDownloadProgress(double pct) => PushEvent(BridgeEventNames.UpdateDownloadProgress, new { pct });
    private void OnHeavyAppsChanged(HeavyAppDetectionState state) => PushEvent(BridgeEventNames.HeavyAppActivityChanged, state);
    private void OnAppProfilesChanged(AppPowerProfileState state) => PushEvent(BridgeEventNames.AppPowerProfileActivityChanged, state);
    private void OnPlanReasonChanged(ActivePlanReasonState state) => PushEvent(BridgeEventNames.ActivePlanReasonChanged, state);
    private void OnPlanConflict(PowerPlanConflictNotification notice) => PushEvent(BridgeEventNames.PowerPlanConflictDetected, notice);
    private void OnPlanHistoryChanged(long revision) => PushEvent(BridgeEventNames.PlanHistoryChanged, new { revision });
    private void OnStandbyCleaned(MemoryStatus memory) => PushEvent(BridgeEventNames.StandbyAutoCleaned, memory);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Dispose();
    }

    public void PushEvent(string name, object data)
    {
        if (IsStopped)
            return;

        if (!string.Equals(name, BridgeEventNames.Metrics, StringComparison.Ordinal))
        {
            PostEvent(name, data);
            return;
        }

        try
        {
            _metricsPublisher.QueueLatest(
                data,
                action => _webView.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    action),
                latest => PostEvent(BridgeEventNames.Metrics, latest));
        }
        catch (Exception ex)
        {
            _pushEventFaulted = Logger.WarnOnce(_pushEventFaulted, "Metrics dispatch failed", ex);
        }
    }

    private void PostEvent(string name, object data)
    {
        if (IsStopped)
            return;

        try
        {
            if (string.Equals(name, BridgeEventNames.Metrics, StringComparison.Ordinal))
                ValidationMetrics.Increment(ValidationCounter.UiMetricPublications);

            string payload = BridgeRpc.FormatEvent(name, data);
            _webView.Dispatcher.Invoke(() =>
            {
                if (IsStopped)
                    return;
                try { _webView.CoreWebView2?.PostWebMessageAsJson(payload); }
                catch { }
            });
            _pushEventFaulted = false;
        }
        catch (Exception ex)
        {
            _pushEventFaulted = Logger.WarnOnce(_pushEventFaulted, "PushEvent failed: " + name, ex);
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        BridgeRpcDispatchResult result = await _dispatcher.DispatchAsync(json, _lifetime.Token);
        if (result.Exception != null && result.LogMessage != null)
            Logger.Error(result.LogMessage, result.Exception);

        if (!IsStopped && result.ReplyJson != null)
            PostReplyJson(result.ReplyJson);
    }

    private void PostReplyJson(string message)
    {
        if (IsStopped)
            return;

        _webView.Dispatcher.Invoke(() =>
        {
            if (IsStopped)
                return;
            try { _webView.CoreWebView2?.PostWebMessageAsJson(message); }
            catch { }
        });
    }

    private async Task<object?> HandleGamingModeRequestedAsync(bool enabled)
    {
        Func<bool, Task<object?>>? handler = GamingModeRequested;
        if (handler == null)
            throw new InvalidOperationException(_loc.T("Error_GamingControlUnavailable"));
        return await handler(enabled);
    }

    private bool IsStopped => _disposed || _lifetime.Token.IsCancellationRequested;
}
