using System.Windows;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager;

public partial class App
{
    private bool _adaptiveResourcesInitialized;

    public ResourcePressureCoordinator ResourcePressure { get; private set; } = null!;

    private void InitializeAdaptiveResourceManagement()
    {
        if (_adaptiveResourcesInitialized) return;
        // A startup failure may already be shutting the app down before all services exist.
        if (Monitor == null || HeavyApps == null || _mainWindow == null) return;

        ResourcePressure = new ResourcePressureCoordinator(Environment.ProcessorCount);
        ResourcePressure.StateChanged += OnResourcePressureStateChanged;
        Monitor.MetricsUpdated += OnAdaptiveResourceMetrics;
        HeavyApps.ActivityChanged += OnAdaptiveHeavyAppActivityChanged;
        _adaptiveResourcesInitialized = true;

        // Prime from the latest sample when available; otherwise the first monitor tick
        // establishes the profile. This subscription is additive and never alters the
        // MonitorService interval used by thermal and power automation.
        if (Monitor.Latest.RamTotalGb > 0)
            ResourcePressure.Observe(Monitor.Latest, HeavyApps.Current.GameActive, HeavyApps.Current.WorkloadActive);

        _mainWindow.InitializeAdaptiveResourceManagement();
        RefreshHardwareSamplingDemand(requestFresh: true);
        Logger.Info("Adaptive resource management initialized.");
    }

    internal void RefreshHardwareSamplingDemand(bool requestFresh = false)
    {
        if (Monitor == null || Settings == null) return;
        // Settings can be saved by background services; WPF surfaces belong to the dispatcher.
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => RefreshHardwareSamplingDemand(requestFresh)));
            return;
        }

        bool mainVisible = _mainWindow?.HasVisibleResourceSurface == true;
        bool widgetsVisible = Widgets?.HasVisibleResourceConsumers == true;
        bool visualDetails = mainVisible || widgetsVisible;
        bool thermalProtection = Settings.Current.ThermalGuard?.Enabled == true;
        bool gpuProcessDetection = Settings.Current.HeavyAppDetection?.Enabled == true;

        Monitor.SetSamplingDemand(new MonitorSamplingDemand(
            visualDetails,
            thermalProtection,
            gpuProcessDetection));

        if (requestFresh && visualDetails)
            Monitor.RequestForegroundRefresh();
    }

    private void OnAdaptiveResourceMetrics(MetricsSnapshot metrics)
    {
        try
        {
            ResourcePressure.Observe(metrics, HeavyApps.Current.GameActive, HeavyApps.Current.WorkloadActive);
        }
        catch (Exception ex)
        {
            // Resource optimization must fail open: safety/automation keeps running.
            Logger.Warn("Resource pressure evaluation failed: " + ex.Message);
        }
    }

    private void OnAdaptiveHeavyAppActivityChanged(HeavyAppDetectionState state)
    {
        StandbyAutoCleaner.ResetAutomaticCandidate();
        FullscreenCoverage?.NotifyProtectedProcessesChanged();
        if (Monitor.Latest.RamTotalGb <= 0) return;
        try
        {
            ResourcePressure.Observe(Monitor.Latest, state.GameActive, state.WorkloadActive);
        }
        catch (Exception ex)
        {
            Logger.Warn("Protected workload evaluation failed: " + ex.Message);
        }
    }

    private void OnResourcePressureStateChanged(ResourcePressureState state)
    {
        Widgets.PushResourceProfile(state);
        // Logging only on operational transitions (the coordinator suppresses per-sample noise).
        Logger.Info($"Resource profile: {state.Profile} ({state.Reason}), " +
                    $"game={state.GameActive}, workload={state.WorkloadActive}, ui={state.UiVisible}");
    }

    private void OnAdaptiveResourceExit(object? sender, ExitEventArgs e)
    {
        if (!_adaptiveResourcesInitialized) return;
        try { Monitor.MetricsUpdated -= OnAdaptiveResourceMetrics; } catch { }
        try { HeavyApps.ActivityChanged -= OnAdaptiveHeavyAppActivityChanged; } catch { }
        try { ResourcePressure.StateChanged -= OnResourcePressureStateChanged; } catch { }
        _adaptiveResourcesInitialized = false;
    }
}
