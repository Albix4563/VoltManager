using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class AppSettings
{
    [JsonPropertyName("standbyAutoCleaner")] public StandbyAutoCleanerSettings StandbyAutoCleaner { get; set; } = new();
    [JsonPropertyName("themeColor")]
    [JsonConverter(typeof(AppThemeColorJsonConverter))]
    public AppThemeColor ThemeColor { get; set; } = AppThemeColor.Blue;
    [JsonPropertyName("masterAutomationEnabled")] public bool MasterAutomationEnabled { get; set; } = true;
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; } = true;
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;
    /// <summary>Schema version of the registered VoltManagerAutostart task (0 = never migrated).</summary>
    [JsonPropertyName("autostartTaskSchemaVersion")] public int AutostartTaskSchemaVersion { get; set; } = 0;
    [JsonPropertyName("updateRepo")] public string UpdateRepo { get; set; } = "Albix4563/power_efficency";
    [JsonPropertyName("rules")] public List<AutomationRule> Rules { get; set; } = DefaultRules();
    // Kept as autoShutdown for backwards compatibility with existing settings.json files.
    [JsonPropertyName("autoShutdown")] public AutoShutdownSettings AutoShutdown { get; set; } = new();
    [JsonPropertyName("autoUpdates")] public AutoUpdateSettings AutoUpdates { get; set; } = new();
    [JsonPropertyName("heavyAppDetection")] public HeavyAppDetectionSettings HeavyAppDetection { get; set; } = new();
    [JsonPropertyName("appPowerProfiles")] public AppPowerProfileSettings AppPowerProfiles { get; set; } = new();
    [JsonPropertyName("keepAwake")] public KeepAwakeSettings KeepAwake { get; set; } = new();
    [JsonPropertyName("powerSourcePlan")] public PowerSourcePlanSettings PowerSourcePlan { get; set; } = new();
    [JsonPropertyName("thermalGuard")] public ThermalGuardSettings ThermalGuard { get; set; } = new();
    [JsonPropertyName("idlePowerGuard")] public IdlePowerGuardSettings IdlePowerGuard { get; set; } = new();
    [JsonPropertyName("cpuAutomation")] public CpuAutomationSettings CpuAutomation { get; set; } = new();
    [JsonPropertyName("globalHotkeys")] public GlobalHotkeySettings GlobalHotkeys { get; set; } = new();
    [JsonPropertyName("widgets")] public WidgetSettings Widgets { get; set; } = new();
    [JsonPropertyName("lanRemoteControl")] public LanRemoteControlSettings LanRemoteControl { get; set; } = new();
    [JsonPropertyName("launcher")] public LauncherSettings Launcher { get; set; } = new();
    // duplicatescheme assigns new GUIDs; map canonical plan -> actual GUID on this machine.
    [JsonPropertyName("planGuidMap")] public Dictionary<string, string> PlanGuidMap { get; set; } = new();
    [JsonPropertyName("override")] public ManualOverride? Override { get; set; }
    [JsonPropertyName("welcomeCompleted")] public bool WelcomeCompleted { get; set; } = false;
    [JsonPropertyName("tourCompleted")] public bool TourCompleted { get; set; } = false;
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("font")] public string Font { get; set; } = "inter";

    public static List<AutomationRule> DefaultRules() => new()
    {
        new AutomationRule { Id = "saver",       Comparison = "lt", ThresholdPct = 20, DurationMinutes = 2, TargetPlan = PlanId.PowerSaver },
        new AutomationRule { Id = "balanced",    Comparison = "gt", ThresholdPct = 30, DurationMinutes = 2, TargetPlan = PlanId.Balanced },
        new AutomationRule { Id = "performance", Comparison = "gt", ThresholdPct = 70, DurationMinutes = 2, TargetPlan = PlanId.Performance },
    };
}
