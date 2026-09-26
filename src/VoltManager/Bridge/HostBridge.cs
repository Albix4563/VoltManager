using System.IO;
using System.Text.Json;
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
    private readonly string? _widgetType;
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
    public event Action? WidgetResizeRequested;
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
        bool subscribeGlobalEvents = true,
        string? widgetType = null)
    {
        _webView = webView;
        _updates = updates;
        _power = power;
        _app = app;
        _loc = app.Loc;
        _subscribeGlobalEvents = subscribeGlobalEvents;
        _widgetType = widgetType;

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
                () => GamingModeStateRequested?.Invoke() ?? _app.GetGamingModeState(),
                HandleGamingModeRequestedAsync,
                () => ExitRequested?.Invoke(),
                () => MinimizeToTrayRequested?.Invoke(),
                () => WidgetDragRequested?.Invoke(),
                () => WidgetResizeRequested?.Invoke(),
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

        _app.Launchers.Changed += OnLaunchersChanged;
        _lifetime.RegisterDetach(() => _app.Launchers.Changed -= OnLaunchersChanged);

        if (!_subscribeGlobalEvents)
        {
            // The main window pushes gaming mode on its own bridge; widgets get the app-level broadcast.
            _app.GamingModeStateChanged += OnGamingModeStateChanged;
            _lifetime.RegisterDetach(() => _app.GamingModeStateChanged -= OnGamingModeStateChanged);
            _app.ScheduledPowerActions.StateChanged += OnScheduledPowerActionChanged;
            _lifetime.RegisterDetach(() => _app.ScheduledPowerActions.StateChanged -= OnScheduledPowerActionChanged);
            return;
        }

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

        string source;
        try { source = e.Source ?? ""; }
        catch { source = ""; }
        if (!WebViewNavigationPolicy.IsTrustedAppUri(source))
        {
            Logger.Warn("Web message ignored from unexpected source: " + source);
            return;
        }

        string json;
        try { json = e.WebMessageAsJson; }
        catch (Exception ex)
        {
            Logger.Error("Could not read web message", ex);
            return;
        }

        if (TryHandleLauncherDropMessage(json, e))
            return;

        await HandleMessageAsync(json);
    }

    private bool TryHandleLauncherDropMessage(string json, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!WidgetSettings.IsLauncherType(_widgetType)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("kind", out var kind) ||
                kind.ValueKind != JsonValueKind.String ||
                !string.Equals(kind.GetString(), "launcherDropFiles", StringComparison.Ordinal))
                return false;
        }
        catch
        {
            return false;
        }

        // Only the bundled widget page may add launchers, never a navigated-to document.
        string source;
        try { source = e.Source ?? ""; }
        catch { source = ""; }
        if (!WebViewNavigationPolicy.IsTrustedAppUri(source))
        {
            Logger.Warn("Launcher drop ignored from unexpected source: " + source);
            return true;
        }

        string category = string.Equals(_widgetType, "apps", StringComparison.OrdinalIgnoreCase) ? "apps" : "games";
        int added = 0;
        int duplicates = 0;
        int rejected = 0;
        bool limitReached = false;

        try
        {
            foreach (object additionalObject in e.AdditionalObjects)
            {
                if (additionalObject is not CoreWebView2File file)
                {
                    rejected++;
                    continue;
                }

                string path;
                try { path = Path.GetFullPath(file.Path); }
                catch
                {
                    rejected++;
                    continue;
                }
                if (!LauncherSettings.IsAllowedCustomPath(path) || !File.Exists(path))
                {
                    rejected++;
                    continue;
                }

                var current = _app.Settings.Current.Launcher.CustomApps;
                string id = LauncherSettings.CustomIdFor(path);
                if (current.Any(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicates++;
                    continue;
                }
                if (current.Count(app => string.Equals(
                        LauncherSettings.NormalizeCategory(app.Category), category, StringComparison.Ordinal)) >= LauncherSettings.MaxPerCategory)
                {
                    limitReached = true;
                    break;
                }

                try
                {
                    _app.Launchers.AddCustomFromDrop(path, category);
                    added++;
                }
                catch (InvalidOperationException ex) when (ex.Message == LauncherDiscoveryService.LimitReachedMessage)
                {
                    limitReached = true;
                    break;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    rejected++;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Launcher drop failed", ex);
        }

        if (added > 0)
            _app.Launchers.NotifyChanged();
        PushEvent(BridgeEventNames.LauncherDropResult, new { added, duplicates, rejected, limitReached });
        return true;
    }

    private void OnDownloadProgress(double pct) => PushEvent(BridgeEventNames.UpdateDownloadProgress, new { pct });
    private void OnHeavyAppsChanged(HeavyAppDetectionState state) => PushEvent(BridgeEventNames.HeavyAppActivityChanged, state);
    private void OnAppProfilesChanged(AppPowerProfileState state) => PushEvent(BridgeEventNames.AppPowerProfileActivityChanged, state);
    private void OnPlanReasonChanged(ActivePlanReasonState state) => PushEvent(BridgeEventNames.ActivePlanReasonChanged, state);
    private void OnPlanConflict(PowerPlanConflictNotification notice) => PushEvent(BridgeEventNames.PowerPlanConflictDetected, notice);
    private void OnPlanHistoryChanged(long revision) => PushEvent(BridgeEventNames.PlanHistoryChanged, new { revision });
    private void OnStandbyCleaned(MemoryStatus memory) => PushEvent(BridgeEventNames.StandbyAutoCleaned, memory);
    private void OnLaunchersChanged() => PushEvent(BridgeEventNames.LaunchersChanged, new { changed = true });
    private void OnGamingModeStateChanged(object state) => PushEvent(BridgeEventNames.GamingModeChanged, state);
    private void OnScheduledPowerActionChanged(ScheduledPowerActionState state) => PushEvent(BridgeEventNames.ScheduledPowerActionChanged, state);

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
            void Post()
            {
                if (IsStopped)
                    return;
                try { _webView.CoreWebView2?.PostWebMessageAsJson(payload); }
                catch { }
            }

            // Events are raised from service threads that may hold their own locks; a
            // synchronous Invoke deadlocks against a UI-thread RPC waiting for that lock.
            if (_webView.Dispatcher.CheckAccess())
                Post();
            else
                _webView.Dispatcher.BeginInvoke(Post);
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
        // Widget bridges have no window-level handler: route through the app instead.
        if (handler == null)
            return await _app.SetGamingModeAsync(enabled);
        return await handler(enabled);
    }

    private bool IsStopped => _disposed || _lifetime.Token.IsCancellationRequested;
}
