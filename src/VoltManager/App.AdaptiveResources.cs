using System.Windows;
using VoltManager.Models;
using VoltManager.Performance;

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
        _adaptiveResourcesInitialized = true;

        // Prime from the latest sample when available; otherwise the first monitor tick
        // establishes the profile. This subscription is additive and never alters the
        // MonitorService interval used by fan/thermal/power automation.
        if (Monitor.Latest.RamTotalGb > 0)
            ResourcePressure.Observe(Monitor.Latest, HeavyApps.Current.GameActive);

        _mainWindow.InitializeAdaptiveResourceManagement();
        Logger.Info("Adaptive resource management initialized.");
    }

    private void OnAdaptiveResourceMetrics(MetricsSnapshot metrics)
    {
        try
        {
            ResourcePressure.Observe(metrics, HeavyApps.Current.GameActive);
        }
        catch (Exception ex)
        {
            // Resource optimization must fail open: safety/automation keeps running.
            Logger.Warn("Resource pressure evaluation failed: " + ex.Message);
        }
    }

    private void OnResourcePressureStateChanged(ResourcePressureState state)
    {
        // Logging only on operational transitions (the coordinator suppresses per-sample noise).
        Logger.Info($"Resource profile: {state.Profile} ({state.Reason}), " +
                    $"game={state.GameActive}, ui={state.UiVisible}");
    }

    private void OnAdaptiveResourceExit(object? sender, ExitEventArgs e)
    {
        if (!_adaptiveResourcesInitialized) return;
        try { Monitor.MetricsUpdated -= OnAdaptiveResourceMetrics; } catch { }
        try { ResourcePressure.StateChanged -= OnResourcePressureStateChanged; } catch { }
        _adaptiveResourcesInitialized = false;
    }
}
