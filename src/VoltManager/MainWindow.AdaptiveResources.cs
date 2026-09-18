using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager;

public partial class MainWindow
{
    private readonly UiMetricsPublisher _adaptiveUiMetricsPublisher = new();
    private readonly WebViewResourceController _webViewResourceController = new();
    private bool _adaptiveResourcesEnabled;
    private bool _adaptiveWindowActive;
    private bool _adaptiveFullscreenCovered;
    private IntPtr _adaptiveHwnd;
    private CoreWebView2? _adaptiveNavigationCore;

    internal void InitializeAdaptiveResourceManagement()
    {
        if (_adaptiveResourcesEnabled) return;
        _adaptiveResourcesEnabled = true;

        _app.ResourcePressure.StateChanged += OnAdaptiveResourceStateChanged;
        IsVisibleChanged += OnAdaptiveWindowVisibilityChanged;
        StateChanged += OnAdaptiveWindowStateChanged;
        Activated += OnAdaptiveWindowActivationChanged;
        Deactivated += OnAdaptiveWindowActivationChanged;
        WebView.CoreWebView2InitializationCompleted += OnAdaptiveCoreWebViewInitialized;
        _app.FullscreenCoverage.CoverageChanged += OnAdaptiveCoverageChanged;
        Closed += OnAdaptiveWindowClosed;

        _adaptiveHwnd = new WindowInteropHelper(this).Handle;
        if (_adaptiveHwnd != IntPtr.Zero)
            _app.FullscreenCoverage.RegisterSurface(_adaptiveHwnd);
        AttachAdaptiveNavigationCore(WebView.CoreWebView2);
        SyncAdaptiveVisibility();
        ScheduleAdaptiveMetricsHook();
    }

    private void OnAdaptiveCoreWebViewInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        AttachAdaptiveNavigationCore(WebView.CoreWebView2);

        // WireWebViewCore subscribes the legacy metrics handler immediately after
        // EnsureCoreWebView2Async completes. ApplicationIdle runs after that continuation,
        // allowing us to atomically replace it with the coalescing publisher.
        ScheduleAdaptiveMetricsHook();
    }

    private void AttachAdaptiveNavigationCore(CoreWebView2? core)
    {
        if (core == null || ReferenceEquals(core, _adaptiveNavigationCore)) return;
        if (_adaptiveNavigationCore != null)
        {
            try { _adaptiveNavigationCore.NavigationCompleted -= OnAdaptiveNavigationCompleted; } catch { }
        }
        _adaptiveNavigationCore = core;
        core.NavigationCompleted += OnAdaptiveNavigationCompleted;
    }

    private void OnAdaptiveNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        ScheduleAdaptiveMetricsHook();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                PublishFreshAdaptiveStateAfterResume();
                if (ValidationEnvironment.IsActive)
                    Logger.Info("Validation marker: fresh adaptive state published after navigation.");
            }));
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
        var plan = _webViewResourceController.Resolve(state.Profile, _webViewVisible, _adaptiveWindowActive);
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
        ResumeDeferredUpdateWorkAfterProtectedSession(state);
    }

    private void PushAdaptiveResourceProfile(ResourcePressureState state)
    {
        var plan = _webViewResourceController.Resolve(state.Profile, state.UiVisible, _adaptiveWindowActive);
        _bridge?.PushEvent("resourceProfileChanged", new
        {
            profile = state.Profile.ToString().ToLowerInvariant(),
            reason = state.Reason,
            gameActive = state.GameActive,
            workloadActive = state.WorkloadActive,
            protectedWorkloadActive = state.ProtectedWorkloadActive,
            uiVisible = state.UiVisible,
            uiActive = _adaptiveWindowActive,
            vramPercent = state.VramPercent,
            reducedEffects = plan.ReducedEffects,
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

    private void OnAdaptiveWindowActivationChanged(object? sender, EventArgs e)
        => SyncAdaptiveVisibility();

    internal bool HasVisibleResourceSurface
        => IsVisible && WindowState != WindowState.Minimized && !_adaptiveFullscreenCovered;

    private void OnAdaptiveCoverageChanged(IntPtr hwnd, bool covered)
    {
        if (hwnd != _adaptiveHwnd || !_adaptiveResourcesEnabled) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_adaptiveResourcesEnabled || hwnd != _adaptiveHwnd) return;
            _adaptiveFullscreenCovered = covered;
            SyncAdaptiveVisibility();
        });
    }

    private void SyncAdaptiveVisibility()
    {
        if (!_adaptiveResourcesEnabled) return;
        bool windowVisible = IsVisible && WindowState != WindowState.Minimized;
        bool visible = windowVisible && !_adaptiveFullscreenCovered;
        bool wasVisible = _webViewVisible;
        bool active = visible && IsActive;
        _webViewVisible = visible;
        _adaptiveWindowActive = active;
        WebView.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (!visible && windowVisible && _adaptiveFullscreenCovered)
            TrySuspendWebView();
        else if (visible && !wasVisible)
            ResumeWebView();
        _app.ResourcePressure.SetUiVisible(visible);
        if (visible) _adaptiveUiMetricsPublisher.ResetCadence();
        PushAdaptiveResourceProfile(_app.ResourcePressure.Current);
        _app.RefreshHardwareSamplingDemand();
    }

    private void PublishFreshAdaptiveStateAfterResume()
    {
        if (!_adaptiveResourcesEnabled || !_webViewVisible) return;
        _adaptiveUiMetricsPublisher.ResetCadence();
        OnAdaptiveMetricsUpdated(_app.Monitor.Latest);
        PushAdaptiveResourceProfile(_app.ResourcePressure.Current);
        _bridge?.PushEvent("thermalGuardChanged", _app.ThermalGuard.Current);
        _bridge?.PushEvent("idlePowerGuardChanged", _app.IdlePowerGuard.Current);
    }

    private void OnAdaptiveWindowClosed(object? sender, EventArgs e)
    {
        if (!_adaptiveResourcesEnabled) return;
        _adaptiveResourcesEnabled = false;
        try { _app.ResourcePressure.StateChanged -= OnAdaptiveResourceStateChanged; } catch { }
        try { _app.Monitor.MetricsUpdated -= OnAdaptiveMetricsUpdated; } catch { }
        try { IsVisibleChanged -= OnAdaptiveWindowVisibilityChanged; } catch { }
        try { StateChanged -= OnAdaptiveWindowStateChanged; } catch { }
        try { Activated -= OnAdaptiveWindowActivationChanged; } catch { }
        try { Deactivated -= OnAdaptiveWindowActivationChanged; } catch { }
        try { WebView.CoreWebView2InitializationCompleted -= OnAdaptiveCoreWebViewInitialized; } catch { }
        try { _app.FullscreenCoverage.CoverageChanged -= OnAdaptiveCoverageChanged; } catch { }
        try { _app.FullscreenCoverage.UnregisterSurface(_adaptiveHwnd); } catch { }
        try
        {
            if (_adaptiveNavigationCore != null)
                _adaptiveNavigationCore.NavigationCompleted -= OnAdaptiveNavigationCompleted;
        }
        catch { }
        _adaptiveNavigationCore = null;
    }
}
