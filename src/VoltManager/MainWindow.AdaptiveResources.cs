using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VoltManager.Models;
using VoltManager.Performance;

namespace VoltManager;

public partial class MainWindow
{
    private readonly UiMetricsPublisher _adaptiveUiMetricsPublisher = new();
    private readonly WebViewResourceController _webViewResourceController = new();
    private bool _adaptiveResourcesEnabled;
    private CoreWebView2? _adaptiveNavigationCore;

    internal void InitializeAdaptiveResourceManagement()
    {
        if (_adaptiveResourcesEnabled) return;
        _adaptiveResourcesEnabled = true;

        _app.ResourcePressure.StateChanged += OnAdaptiveResourceStateChanged;
        IsVisibleChanged += OnAdaptiveWindowVisibilityChanged;
        StateChanged += OnAdaptiveWindowStateChanged;
        WebView.CoreWebView2InitializationCompleted += OnAdaptiveCoreWebViewInitialized;
        Closed += OnAdaptiveWindowClosed;

        SyncAdaptiveVisibility();
        ScheduleAdaptiveMetricsHook();
    }

    private void OnAdaptiveCoreWebViewInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        var core = WebView.CoreWebView2;
        if (core != null && !ReferenceEquals(core, _adaptiveNavigationCore))
        {
            _adaptiveNavigationCore = core;
            core.NavigationCompleted += OnAdaptiveNavigationCompleted;
        }

        // WireWebViewCore subscribes the legacy metrics handler immediately after
        // EnsureCoreWebView2Async completes. ApplicationIdle runs after that continuation,
        // allowing us to atomically replace it with the coalescing publisher.
        ScheduleAdaptiveMetricsHook();
    }

    private void OnAdaptiveNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        ScheduleAdaptiveMetricsHook();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => PushAdaptiveResourceProfile(_app.ResourcePressure.Current)));
    }

    private void ScheduleAdaptiveMetricsHook()
    {
        if (!_adaptiveResourcesEnabled) return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(EnsureAdaptiveMetricsHook));
    }

    private void EnsureAdaptiveMetricsHook()
    {
        if (!_adaptiveResourcesEnabled || !_hostEventsWired) return;
        // Idempotent replacement. The original handler mixes UI transport with the
        // gaming reminder; OnAdaptiveMetricsUpdated preserves the reminder separately.
        _app.Monitor.MetricsUpdated -= OnMetricsUpdated;
        _app.Monitor.MetricsUpdated -= OnAdaptiveMetricsUpdated;
        _app.Monitor.MetricsUpdated += OnAdaptiveMetricsUpdated;
        _adaptiveUiMetricsPublisher.ResetCadence();
        PushAdaptiveResourceProfile(_app.ResourcePressure.Current);
    }

    private void OnAdaptiveMetricsUpdated(MetricsSnapshot metrics)
    {
        var state = _app.ResourcePressure.Current;
        var plan = _webViewResourceController.Resolve(state.Profile, _webViewVisible);
        if (_adaptiveUiMetricsPublisher.TryTake(metrics, plan, DateTime.UtcNow, out var snapshot) && snapshot != null)
            _bridge?.PushEvent("metrics", snapshot);

        // Keep the existing manual-performance gaming reminder at the safety sampling
        // cadence; only the WebView transport above is downsampled.
        if (_gamingReminder.ObserveCpu(metrics.Cpu, DateTime.UtcNow) != GamingModeReminderDecision.Prompt)
            return;
        if (Interlocked.Exchange(ref _gamingReminderPromptRunning, 1) == 1)
            return;

        _ = Dispatcher.InvokeAsync(() =>
        {
            try { ShowGamingModeReminder(metrics.Cpu); }
            finally { Interlocked.Exchange(ref _gamingReminderPromptRunning, 0); }
        });
    }

    private void OnAdaptiveResourceStateChanged(ResourcePressureState state)
    {
        if (!_adaptiveResourcesEnabled) return;
        EnsureAdaptiveMetricsHook();
        _adaptiveUiMetricsPublisher.ResetCadence();
        PushAdaptiveResourceProfile(state);
    }

    private void PushAdaptiveResourceProfile(ResourcePressureState state)
    {
        var plan = _webViewResourceController.Resolve(state.Profile, state.UiVisible);
        _bridge?.PushEvent("resourceProfileChanged", new
        {
            profile = state.Profile.ToString().ToLowerInvariant(),
            reason = state.Reason,
            gameActive = state.GameActive,
            uiVisible = state.UiVisible,
            metricsIntervalMs = plan.PublishMetrics ? (int)plan.MetricsInterval.TotalMilliseconds : 0,
            allowProcessPolling = plan.AllowProcessPolling,
            processPollingIntervalMs = plan.AllowProcessPolling
                ? (int)plan.ProcessPollingInterval.TotalMilliseconds
                : 0,
        });
    }

    private void OnAdaptiveWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => SyncAdaptiveVisibility();

    private void OnAdaptiveWindowStateChanged(object? sender, EventArgs e)
        => SyncAdaptiveVisibility();

    private void SyncAdaptiveVisibility()
    {
        if (!_adaptiveResourcesEnabled) return;
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        _webViewVisible = visible;
        _app.ResourcePressure.SetUiVisible(visible);
        if (visible) _adaptiveUiMetricsPublisher.ResetCadence();
    }

    private void OnAdaptiveWindowClosed(object? sender, EventArgs e)
    {
        if (!_adaptiveResourcesEnabled) return;
        _adaptiveResourcesEnabled = false;
        try { _app.ResourcePressure.StateChanged -= OnAdaptiveResourceStateChanged; } catch { }
        try { _app.Monitor.MetricsUpdated -= OnAdaptiveMetricsUpdated; } catch { }
        try { IsVisibleChanged -= OnAdaptiveWindowVisibilityChanged; } catch { }
        try { StateChanged -= OnAdaptiveWindowStateChanged; } catch { }
        try { WebView.CoreWebView2InitializationCompleted -= OnAdaptiveCoreWebViewInitialized; } catch { }
        try
        {
            if (_adaptiveNavigationCore != null)
                _adaptiveNavigationCore.NavigationCompleted -= OnAdaptiveNavigationCompleted;
        }
        catch { }
        _adaptiveNavigationCore = null;
    }
}
