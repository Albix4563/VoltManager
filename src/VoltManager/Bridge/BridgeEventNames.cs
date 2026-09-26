namespace VoltManager.Bridge;

public static class BridgeEventNames
{
    public const string ActivePlanChanged = "activePlanChanged";
    public const string ActivePlanReasonChanged = "activePlanReasonChanged";
    public const string AppPowerProfileActivityChanged = "appPowerProfileActivityChanged";
    public const string AppUpdated = "appUpdated";
    public const string AutomationStateChanged = "automationStateChanged";
    public const string CpuAutomationStateChanged = "cpuAutomationStateChanged";
    public const string FontChanged = "fontChanged";
    public const string GamingModeChanged = "gamingModeChanged";
    public const string GlobalHotkeysChanged = "globalHotkeysChanged";
    public const string HeavyAppActivityChanged = "heavyAppActivityChanged";
    public const string IdlePowerGuardChanged = "idlePowerGuardChanged";
    public const string KeepAwakeChanged = "keepAwakeChanged";
    public const string LanguageChanged = "languageChanged";
    public const string LaunchersChanged = "launchersChanged";
    public const string LauncherDropResult = "launcherDropResult";
    public const string ManualOverrideChanged = "manualOverrideChanged";
    public const string Metrics = "metrics";
    public const string PlanHistoryChanged = "planHistoryChanged";
    public const string PowerPlanConflictDetected = "powerPlanConflictDetected";
    public const string PowerSourcePlanChanged = "powerSourcePlanChanged";
    public const string ResourceProfileChanged = "resourceProfileChanged";
    public const string ScheduledPowerActionChanged = "scheduledPowerActionChanged";
    public const string StandbyAutoCleaned = "standbyAutoCleaned";
    public const string ThemeChanged = "themeChanged";
    public const string ThermalGuardChanged = "thermalGuardChanged";
    public const string UpdateAvailable = "updateAvailable";
    public const string UpdateDownloadProgress = "updateDownloadProgress";
    public const string WidgetsStateChanged = "widgetsStateChanged";
    public const string WidgetTopmostChanged = "widgetTopmostChanged";

    public static IReadOnlyCollection<string> All { get; } = Array.AsReadOnly(new[]
    {
        ActivePlanChanged, ActivePlanReasonChanged, AppPowerProfileActivityChanged, AppUpdated,
        AutomationStateChanged, CpuAutomationStateChanged, FontChanged, GamingModeChanged,
        GlobalHotkeysChanged, HeavyAppActivityChanged, IdlePowerGuardChanged, KeepAwakeChanged,
        LanguageChanged, LauncherDropResult, LaunchersChanged, ManualOverrideChanged, Metrics, PlanHistoryChanged,
        PowerPlanConflictDetected, PowerSourcePlanChanged, ResourceProfileChanged,
        ScheduledPowerActionChanged, StandbyAutoCleaned, ThemeChanged, ThermalGuardChanged,
        UpdateAvailable, UpdateDownloadProgress, WidgetsStateChanged, WidgetTopmostChanged,
    });
}
