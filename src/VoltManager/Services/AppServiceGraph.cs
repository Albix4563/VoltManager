using VoltManager.Localization;
using VoltManager.Services.LanRemote;

namespace VoltManager.Services;

public sealed class AppServiceGraph
{
    public AppServiceGraph(
        SettingsService settings,
        LocalizationService localization,
        ThemeService theme,
        PowerPlanService power,
        PowerAwakeService awake,
        IHardwareAccess hardwareAccess,
        MonitorService monitor,
        UpdateService updates,
        StartupService autoStart,
        AutomationEngine automation,
        HeavyAppDetectionService heavyApps,
        ProtectedFullscreenCoverageService fullscreenCoverage,
        AppPowerProfileService appProfiles,
        PowerSourcePlanService powerSourcePlans,
        ThermalGuardService thermalGuard,
        IdlePowerGuardService idlePowerGuard,
        StandbyAutoCleanerService standbyAutoCleaner,
        PowerFlowService powerFlow,
        BatteryHistoryService batteryHistory,
        ScheduledPowerActionService scheduledPowerActions,
        LanRemoteControlService lanRemoteControl,
        RemoteCommandService remoteCommands,
        PowerRequestCoordinator powerRequests,
        WidgetManager widgets)
    {
        Settings = settings;
        Localization = localization;
        Theme = theme;
        Power = power;
        Awake = awake;
        HardwareAccess = hardwareAccess;
        Monitor = monitor;
        Updates = updates;
        AutoStart = autoStart;
        Automation = automation;
        HeavyApps = heavyApps;
        FullscreenCoverage = fullscreenCoverage;
        AppProfiles = appProfiles;
        PowerSourcePlans = powerSourcePlans;
        ThermalGuard = thermalGuard;
        IdlePowerGuard = idlePowerGuard;
        StandbyAutoCleaner = standbyAutoCleaner;
        PowerFlow = powerFlow;
        BatteryHistory = batteryHistory;
        ScheduledPowerActions = scheduledPowerActions;
        LanRemoteControl = lanRemoteControl;
        RemoteCommands = remoteCommands;
        PowerRequests = powerRequests;
        Widgets = widgets;
    }

    public SettingsService Settings { get; }
    public LocalizationService Localization { get; }
    public ThemeService Theme { get; }
    public PowerPlanService Power { get; }
    public PowerAwakeService Awake { get; }
    public IHardwareAccess HardwareAccess { get; }
    public MonitorService Monitor { get; }
    public UpdateService Updates { get; }
    public StartupService AutoStart { get; }
    public AutomationEngine Automation { get; }
    public HeavyAppDetectionService HeavyApps { get; }
    public ProtectedFullscreenCoverageService FullscreenCoverage { get; }
    public AppPowerProfileService AppProfiles { get; }
    public PowerSourcePlanService PowerSourcePlans { get; }
    public ThermalGuardService ThermalGuard { get; }
    public IdlePowerGuardService IdlePowerGuard { get; }
    public StandbyAutoCleanerService StandbyAutoCleaner { get; }
    public PowerFlowService PowerFlow { get; }
    public BatteryHistoryService BatteryHistory { get; }
    public ScheduledPowerActionService ScheduledPowerActions { get; }
    public LanRemoteControlService LanRemoteControl { get; }
    public RemoteCommandService RemoteCommands { get; }
    public PowerRequestCoordinator PowerRequests { get; }
    public WidgetManager Widgets { get; }
}
