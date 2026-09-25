using System.Diagnostics;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager.Bridge;

internal static class BridgeHandlerFactory
{
    public static IReadOnlyCollection<IBridgeRpcHandler> Create(
        HardwareInfoService hardware,
        PowerPlanService power,
        SettingsService settings,
        UpdateService updates,
        StartupService startup,
        MonitorService monitor,
        App app,
        LocalizationService loc,
        IBridgeFileDialogService dialogs,
        Func<object> getGamingMode,
        Func<bool, Task<object?>> setGamingMode,
        Action requestExit,
        Action requestMinimize,
        Action beginWidgetDrag,
        Action<bool> setWidgetTopmost,
        Action closeWidget)
    {
        var startupApps = new StartupAppsService();
        var planParams = new PowerPlanParameterService(power);
        var memoryOptimizer = new MemoryOptimizerService();
        var batteryHealth = new BatteryHealthService();
        var powerFlow = new PowerFlowService();
        var batteryPowerSmoother = new BatteryPowerSmoother();
        var brightness = new BrightnessService();

        var settingsHandler = new SettingsRpcHandler(
            settings,
            loc,
            new SettingsRpcActions(
                () => app.Theme.GetWebTheme(),
                () => app.Theme.GetWebThemeCatalog(),
                () => { app.RefreshAppPowerProfiles(); },
                () => { app.RefreshHeavyAppDetection(); },
                () => app.Dispatcher.Invoke(app.SetupJumpListPublic),
                startup.IsEnabled,
                startup.SetStartWithWindows,
                () => DateTime.UtcNow),
            dialogs);

        var widgetHandler = new WidgetRpcHandler(new WidgetRpcActions(
            () => app.Widgets.GetSnapshot(),
            app.Widgets.SetEnabled,
            app.Widgets.SetMasterEnabled,
            app.Widgets.SetPinned,
            app.Widgets.SetSize,
            app.Widgets.SetPlacement,
            app.Widgets.ResetPosition,
            beginWidgetDrag,
            setWidgetTopmost,
            closeWidget));

        var energyHandler = new EnergyRpcHandler(
            settings,
            loc,
            new EnergyRpcActions(
                () => batteryHealth.GetHealth(),
                () => GetBatteryPower(app, powerFlow, batteryPowerSmoother),
                () => app.BatteryHistory.GetHistory(),
                () => brightness.GetBrightness(),
                percent => brightness.SetBrightness(percent),
                power.CheckDefaultPlans,
                power.RestoreDefaultPlans,
                power.GetActivePlan,
                () => app.GetActivePlanReason(),
                () => power.History.GetSnapshot(),
                power.History.Clear,
                () => planParams.ListPlans(),
                () => app.Awake.GetState(),
                app.SetKeepAwake,
                app.Awake.SetSafetyOptions,
                () => app.CpuAutomationState,
                (plan, duration) => app.SetManualOverride(plan, duration),
                app.ClearManualOverride,
                () => settings.Current.Override,
                () => app.GetPowerSourcePlanState(),
                app.SetPowerSourcePlanSwitch,
                () => app.ThermalGuard.Current,
                app.ThermalGuard.SetEnabled,
                app.ThermalGuard.ApplySettings,
                () => app.IdlePowerGuard.Current,
                app.IdlePowerGuard.SetEnabled,
                app.IdlePowerGuard.ApplySettings,
                () => app.ScheduledPowerActions.GetState(),
                app.ScheduledPowerActions.ExecuteNow,
                app.ScheduledPowerActions.ScheduleAfter,
                app.ScheduledPowerActions.ScheduleDaily,
                app.ScheduledPowerActions.Cancel,
                planParams.GetPlanTimeouts,
                planParams.GetPlanParameters,
                planParams.SetPlanParameter,
                () => DateTime.UtcNow),
            dialogs);

        var monitoringHandler = new MonitoringRpcHandler(
            new MonitoringRpcActions(
                () => hardware.GetSystemInfo(),
                () => app.GetHeavyAppStatus(),
                () => app.RefreshHeavyAppDetection(),
                () => app.GetAppPowerProfileStatus(),
                cancellationToken => dialogs.OpenFileAsync(
                    new BridgeOpenFileRequest(
                        loc.T("FilePicker_AppProfileTitle"),
                        loc.T("FilePicker_AppProfileFilter"),
                        CheckFileExists: true,
                        Multiselect: false),
                    cancellationToken),
                count => monitor.GetTopProcesses(count, app.ResourcePressure?.Current),
                () => memoryOptimizer.GetMemoryStatus(),
                () => app.StandbyAutoCleaner.PurgeManual(),
                OpenLogFolder,
                () => BuildDiagnosticsReport(
                    hardware, power, settings, monitor, app, memoryOptimizer,
                    batteryHealth, powerFlow, batteryPowerSmoother),
                () => DateTime.Now),
            dialogs);

        var updateHandler = new UpdateRpcHandler(
            loc,
            new UpdateRpcActions(
                updates.CheckForUpdatesAsync,
                updates.GetReleaseHistoryAsync,
                updates.DownloadUpdateAsync,
                app.IsHeavyAppSessionActive,
                app.DeferUpdateUntilGameEnds,
                (path, args) =>
                {
                    Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true });
                    Logger.Info("Update installer launched; exiting for update.");
                },
                requestExit));

        var applicationHandler = new ApplicationRpcHandler(
            loc,
            new ApplicationRpcActions(
                getGamingMode,
                setGamingMode,
                () => startupApps.GetStartupApps(),
                cancellationToken => dialogs.OpenFileAsync(
                    new BridgeOpenFileRequest(
                        "Seleziona applicazione da avviare con Windows",
                        "Applicazioni (*.exe)|*.exe|Collegamenti (*.lnk)|*.lnk|Script avviabili (*.bat;*.cmd)|*.bat;*.cmd|Tutti i file (*.*)|*.*",
                        CheckFileExists: true,
                        Multiselect: false),
                    cancellationToken),
                startupApps.AddManagedStartupApp,
                startupApps.SetStartupAppEnabled,
                startupApps.RemoveManagedStartupApp,
                Logger.Error,
                url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
                requestExit,
                requestMinimize));

        var lanRemoteHandler = new LanRemoteControlRpcHandler(new LanRemoteControlRpcActions(
            app.LanRemoteControl.GetState,
            app.LanRemoteControl.SetEnabledAsync,
            app.LanRemoteControl.SetPermissions,
            app.LanRemoteControl.GeneratePin,
            app.LanRemoteControl.SetPin));

        return
        [
            settingsHandler,
            widgetHandler,
            energyHandler,
            monitoringHandler,
            updateHandler,
            applicationHandler,
            lanRemoteHandler,
        ];
    }

    private static BatteryPowerState GetBatteryPower(
        App app,
        PowerFlowService powerFlow,
        BatteryPowerSmoother smoother)
    {
        BatteryPowerState raw = powerFlow.GetState();
        IReadOnlyList<BatteryHistorySample>? history = null;
        try { history = app.BatteryHistory.GetHistory(); }
        catch { }
        return smoother.Apply(raw, history, DateTime.UtcNow);
    }

    private static (bool success, string? path, string? error) OpenLogFolder()
    {
        bool success = DiagnosticsReportService.TryOpenLogFolder(out string? path, out string? error);
        return (success, path, error);
    }

    private static string BuildDiagnosticsReport(
        HardwareInfoService hardware,
        PowerPlanService power,
        SettingsService settings,
        MonitorService monitor,
        App app,
        MemoryOptimizerService memoryOptimizer,
        BatteryHealthService batteryHealth,
        PowerFlowService powerFlow,
        BatteryPowerSmoother batteryPowerSmoother)
    {
        var plan = power.GetActivePlan();
        string planSummary = plan == null
            ? "(none)"
            : $"{plan.PlanId?.ToString() ?? "?"}  {plan.Name}  [{plan.Guid}]  active={plan.IsActive}";

        BatteryPowerState? batteryPower = null;
        try { batteryPower = GetBatteryPower(app, powerFlow, batteryPowerSmoother); }
        catch { }

        var snapshot = new DiagnosticsSnapshot
        {
            AppVersion = DiagnosticsReportService.ResolveAppVersion(),
            LogPath = DiagnosticsReportService.LogFilePath,
            SystemInfo = hardware.GetSystemInfo(),
            Metrics = monitor.Latest,
            ActivePlanSummary = planSummary,
            KeepAwake = app.Awake.GetState(),
            PowerSource = app.PowerSourcePlans.Current,
            ThermalGuard = app.ThermalGuard.Current,
            IdlePowerGuard = app.IdlePowerGuard.Current,
            CpuAutomation = app.CpuAutomationState,
            BatteryPower = batteryPower,
            BatteryHealth = batteryHealth.GetHealth(),
            Memory = memoryOptimizer.GetMemoryStatus(),
            SettingsJson = DiagnosticsReportService.SanitizeSettingsJson(settings.Current),
        };

        return DiagnosticsReportService.BuildReport(snapshot);
    }
}
