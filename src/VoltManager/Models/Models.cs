using System.Text.Json.Serialization;

namespace VoltManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerActionType
{
    Shutdown,
    Restart,
    Sleep
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerMode
{
    Relative,
    Daily
}

public enum PlanId
{
    PowerSaver,
    Balanced,
    Performance
}

public record MetricsSnapshot
{
    [JsonPropertyName("cpu")] public double Cpu { get; init; }
    [JsonPropertyName("gpu")] public double Gpu { get; init; }
    [JsonPropertyName("gpuAvailable")] public bool GpuAvailable { get; init; }
    [JsonPropertyName("ramPct")] public double RamPct { get; init; }
    [JsonPropertyName("ramUsedGb")] public double RamUsedGb { get; init; }
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("disk")] public double Disk { get; init; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public double? GpuTemp { get; init; }
    [JsonPropertyName("cpuClock")] public double? CpuClock { get; init; }
    [JsonPropertyName("ramClock")] public double? RamClock { get; init; }
    [JsonPropertyName("sensorsAvailable")] public bool SensorsAvailable { get; init; }
    [JsonPropertyName("sensors")] public List<SensorReading> Sensors { get; init; } = new();
}

public record SensorReading
{
    [JsonPropertyName("identifier")] public string? Identifier { get; init; }
    [JsonPropertyName("hardware")] public string Hardware { get; init; } = "";  // device name
    [JsonPropertyName("category")] public string Category { get; init; } = "";  // cpu|gpu|storage|motherboard
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";          // temp|fan
    [JsonPropertyName("value")] public double Value { get; init; }
    [JsonPropertyName("controlAvailable")] public bool ControlAvailable { get; init; }
    [JsonPropertyName("controlMode")] public string? ControlMode { get; init; }
    [JsonPropertyName("controlPercent")] public double? ControlPercent { get; init; }
    [JsonPropertyName("controlMin")] public double? ControlMin { get; init; }
    [JsonPropertyName("controlMax")] public double? ControlMax { get; init; }
}

public record SystemInfo
{
    [JsonPropertyName("cpuName")] public string CpuName { get; init; } = "";
    [JsonPropertyName("gpuName")] public string GpuName { get; init; } = "";
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("osVersion")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("hasBattery")] public bool HasBattery { get; init; }
    [JsonPropertyName("logicalCores")] public int LogicalCores { get; init; }
}

public class AutomationRule
{
    // Comparison: "lt" fires when CPU below threshold, "gt" when above.
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("comparison")] public string Comparison { get; set; } = "gt";
    [JsonPropertyName("thresholdPct")] public double ThresholdPct { get; set; }
    [JsonPropertyName("durationMinutes")] public double DurationMinutes { get; set; } = 1;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; }
}

public class ManualOverride
{
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    [JsonPropertyName("expiresAtUtc")] public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActive(DateTime nowUtc) => ExpiresAtUtc == null || ExpiresAtUtc > nowUtc;
}

public class AutoShutdownSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

    [JsonPropertyName("mode")]
    public ScheduledPowerMode Mode { get; set; } = ScheduledPowerMode.Daily;

    [JsonPropertyName("action")]
    public ScheduledPowerActionType Action { get; set; } = ScheduledPowerActionType.Shutdown;

    // Legacy string action for backwards compat during migration — normalized on load.
    [JsonPropertyName("actionLegacy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionLegacy { get; set; }

    [JsonPropertyName("time")] public string Time { get; set; } = "23:00";

    [JsonPropertyName("executeAtUtc")] public DateTime? ExecuteAtUtc { get; set; }

    [JsonPropertyName("delayMinutes")] public int? DelayMinutes { get; set; }

    [JsonPropertyName("createdAtUtc")] public DateTime? CreatedAtUtc { get; set; }

    [JsonPropertyName("lastTriggeredLocalDate")] public string? LastTriggeredLocalDate { get; set; }
}

/// <summary>Public state for GUI/tray — decoupled from persistence model.</summary>
public record ScheduledPowerActionState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    [JsonPropertyName("mode")] public ScheduledPowerMode Mode { get; init; }

    [JsonPropertyName("action")] public ScheduledPowerActionType Action { get; init; }

    [JsonPropertyName("executeAtUtc")] public DateTime? ExecuteAtUtc { get; init; }

    [JsonPropertyName("delayMinutes")] public int? DelayMinutes { get; init; }

    [JsonPropertyName("remainingSeconds")] public long RemainingSeconds { get; init; }

    [JsonPropertyName("dailyTime")] public string? DailyTime { get; init; }

    [JsonPropertyName("expired")] public bool Expired { get; init; }
}

public class AutoUpdateSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("silentInstallEnabled")] public bool SilentInstallEnabled { get; set; } = true;
    [JsonPropertyName("updateChannel")] public string UpdateChannel { get; set; } = "stable";
    
    [JsonPropertyName("previewChannel")]
    public bool PreviewChannel 
    { 
        get => UpdateChannel == "preview"; 
        set { if (value) UpdateChannel = "preview"; } 
    }

    [JsonIgnore] public bool IsPreview => UpdateChannel == "preview";
    [JsonIgnore] public bool IsDev => UpdateChannel == "dev";

    [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 30;
    [JsonPropertyName("snoozedUntilUtc")] public DateTime? SnoozedUntilUtc { get; set; }
    [JsonPropertyName("skippedVersion")] public string? SkippedVersion { get; set; }
}

public class HeavyAppDetectionSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("useWindowsGpuPreferences")] public bool UseWindowsGpuPreferences { get; set; } = true;
    [JsonPropertyName("useGameInstallHeuristics")] public bool UseGameInstallHeuristics { get; set; } = true;
    [JsonPropertyName("useResourceHeuristics")] public bool UseResourceHeuristics { get; set; } = true;
    [JsonPropertyName("minWorkingSetMb")] public int MinWorkingSetMb { get; set; } = 1536;
    /// <summary>Executables or folders the user always wants treated as a game.</summary>
    [JsonPropertyName("alwaysGamePaths")] public List<string> AlwaysGamePaths { get; set; } = new();
    /// <summary>Executables or folders that must never be detected. Wins over the include list.</summary>
    [JsonPropertyName("neverGamePaths")] public List<string> NeverGamePaths { get; set; } = new();
}

public class AppPowerProfileRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.Performance;
}

public class AppPowerProfileSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("rules")] public List<AppPowerProfileRule> Rules { get; set; } = new();
}

public class KeepAwakeSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("lastChangedUtc")] public DateTime? LastChangedUtc { get; set; }

    /// <summary>
    /// When true, keep-awake turns itself off as soon as the machine runs on battery
    /// so overnight downloads do not silently drain the pack.
    /// </summary>
    [JsonPropertyName("autoDisableOnBattery")] public bool AutoDisableOnBattery { get; set; } = true;

    /// <summary>
    /// Optional hard cap in minutes (0 = unlimited). Measured from lastChangedUtc
    /// when the feature was last enabled.
    /// </summary>
    [JsonPropertyName("maxMinutes")] public int MaxMinutes { get; set; } = 0;

    public void Normalize()
    {
        if (MaxMinutes < 0) MaxMinutes = 0;
        if (MaxMinutes > 24 * 60) MaxMinutes = 24 * 60; // cap at 24h
    }
}

public class PowerSourcePlanSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("pluggedPlan")] public PlanId PluggedPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("unpluggedMode")] public string UnpluggedMode { get; set; } = "previous";
}

public class CpuAutomationSettings
{
    public const int MinSampleIntervalSeconds = 1;
    public const int MaxSampleIntervalSeconds = 60;

    [JsonPropertyName("sampleIntervalSeconds")] public int SampleIntervalSeconds { get; set; } = 1;

    public void Normalize()
    {
        SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, MinSampleIntervalSeconds, MaxSampleIntervalSeconds);
    }
}

public record CpuAutomationState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("sampleIntervalSeconds")] public int SampleIntervalSeconds { get; init; } = 1;
    [JsonPropertyName("rawCpu")] public double RawCpu { get; init; }
    [JsonPropertyName("averageCpu")] public double AverageCpu { get; init; }
    [JsonPropertyName("sampledAtUtc")] public DateTime? SampledAtUtc { get; init; }
    [JsonPropertyName("candidateRuleId")] public string? CandidateRuleId { get; init; }
    [JsonPropertyName("candidateTargetPlan")] public PlanId? CandidateTargetPlan { get; init; }
    [JsonPropertyName("activePlan")] public PlanId? ActivePlan { get; init; }
    [JsonPropertyName("manualOverrideActive")] public bool ManualOverrideActive { get; init; }
}

public class WidgetItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    // Off until the user (or installer) explicitly enables a widget type.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("pinned")] public bool Pinned { get; set; } = false;
    [JsonPropertyName("size")] public string Size { get; set; } = "medium";
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("monitorId")] public string? MonitorId { get; set; }
    [JsonPropertyName("monitorName")] public string? MonitorName { get; set; }
    [JsonPropertyName("monitorNumber")] public int? MonitorNumber { get; set; }
    // null = legacy item not yet migrated to anchor/offset placement.
    [JsonPropertyName("anchor")] public string? Anchor { get; set; }
    [JsonPropertyName("offsetX")] public double OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double OffsetY { get; set; }
}

public class WidgetSettings
{
    public static readonly string[] Types = ["clock", "calendar", "usage", "temps", "power", "plans"];
    public static readonly string[] Sizes = ["mini", "medium", "large"];
    public static readonly string[] Anchors =
    [
        "topLeft", "topCenter", "topRight",
        "middleLeft", "center", "middleRight",
        "bottomLeft", "bottomCenter", "bottomRight",
    ];

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("items")] public List<WidgetItem> Items { get; set; } = DefaultItems();

    public static List<WidgetItem> DefaultItems() => Types.Select(t => new WidgetItem { Type = t }).ToList();

    public static bool IsKnownType(string? type)
        => Types.Contains(type ?? "", StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownAnchor(string? anchor)
        => Anchors.Contains(anchor ?? "", StringComparer.OrdinalIgnoreCase);

    public static string NormalizeSize(string? size)
        => Sizes.FirstOrDefault(s => string.Equals(s, size, StringComparison.OrdinalIgnoreCase)) ?? "medium";

    public static string NormalizeAnchor(string? anchor)
        => Anchors.FirstOrDefault(a => string.Equals(a, anchor, StringComparison.OrdinalIgnoreCase)) ?? "topRight";

    public void Normalize()
    {
        Items ??= new List<WidgetItem>();

        var byType = new Dictionary<string, WidgetItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item == null || !IsKnownType(item.Type)) continue;
            item.Type = Types.First(t => string.Equals(t, item.Type, StringComparison.OrdinalIgnoreCase));
            item.Size = NormalizeSize(item.Size);
            if (double.IsNaN(item.X ?? 0) || double.IsInfinity(item.X ?? 0)) item.X = null;
            if (double.IsNaN(item.Y ?? 0) || double.IsInfinity(item.Y ?? 0)) item.Y = null;
            if (item.Anchor != null) item.Anchor = NormalizeAnchor(item.Anchor);
            if (!double.IsFinite(item.OffsetX)) item.OffsetX = 0;
            if (!double.IsFinite(item.OffsetY)) item.OffsetY = 0;
            if (item.MonitorNumber is <= 0) item.MonitorNumber = null;
            item.MonitorId = string.IsNullOrWhiteSpace(item.MonitorId) ? null : item.MonitorId.Trim();
            item.MonitorName = string.IsNullOrWhiteSpace(item.MonitorName) ? null : item.MonitorName.Trim();
            byType.TryAdd(item.Type, item);
        }

        Items = Types.Select(t => byType.TryGetValue(t, out var item) ? item : new WidgetItem { Type = t }).ToList();
    }

    public WidgetItem GetOrAdd(string type)
    {
        Normalize();
        var item = Items.FirstOrDefault(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
        if (item != null) return item;

        item = new WidgetItem { Type = type };
        Items.Add(item);
        Normalize();
        return Items.First(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
    }
}

public record KeepAwakeState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("applied")] public bool Applied { get; init; }
    [JsonPropertyName("lastChangedUtc")] public DateTime? LastChangedUtc { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    // Safety options (echoed for the UI)
    [JsonPropertyName("autoDisableOnBattery")] public bool AutoDisableOnBattery { get; init; } = true;
    [JsonPropertyName("maxMinutes")] public int MaxMinutes { get; init; }
    // Seconds left before auto-timeout; null when unlimited or inactive.
    [JsonPropertyName("remainingSeconds")] public long? RemainingSeconds { get; init; }
    // none | battery | timeout
    [JsonPropertyName("lastAutoDisableReason")] public string? LastAutoDisableReason { get; init; }
}

public record PowerSourcePlanState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("powerSourceKnown")] public bool PowerSourceKnown { get; init; }
    [JsonPropertyName("pluggedIn")] public bool PluggedIn { get; init; }
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; init; }
    [JsonPropertyName("lowBatteryActive")] public bool LowBatteryActive { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("pluggedPlan")] public PlanId PluggedPlan { get; init; } = PlanId.Performance;
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    [JsonPropertyName("targetPlan")] public PlanId? TargetPlan { get; init; }
    [JsonPropertyName("manualOverrideActive")] public bool ManualOverrideActive { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public class StandbyAutoCleanerSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("thresholdGb")] public double ThresholdGb { get; set; } = 2.0;
    [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 60;
    [JsonPropertyName("lastPurgedUtc")] public DateTime? LastPurgedUtc { get; set; }
}

/// <summary>
/// When CPU/GPU stay hot, force a cooler power plan to cut boost heat and draw.
/// Hysteresis (cool threshold) avoids flapping around the trip point.
/// </summary>
public class ThermalGuardSettings
{
    public const double MinThresholdC = 60;
    public const double MaxThresholdC = 105;
    public const int MinHoldSeconds = 5;
    public const int MaxHoldSeconds = 300;

    // Off by default: requires readable sensors; user opts in.
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("thresholdCelsius")] public double ThresholdCelsius { get; set; } = 90;
    // Must be below threshold; default = threshold - 8 °C.
    [JsonPropertyName("coolThresholdCelsius")] public double CoolThresholdCelsius { get; set; } = 82;
    [JsonPropertyName("holdSeconds")] public int HoldSeconds { get; set; } = 20;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.PowerSaver;
    [JsonPropertyName("watchGpu")] public bool WatchGpu { get; set; } = true;

    public void Normalize()
    {
        ThresholdCelsius = Math.Clamp(ThresholdCelsius, MinThresholdC, MaxThresholdC);
        CoolThresholdCelsius = Math.Clamp(CoolThresholdCelsius, MinThresholdC - 15, ThresholdCelsius - 1);
        if (CoolThresholdCelsius >= ThresholdCelsius)
            CoolThresholdCelsius = Math.Max(MinThresholdC - 15, ThresholdCelsius - 8);
        HoldSeconds = Math.Clamp(HoldSeconds, MinHoldSeconds, MaxHoldSeconds);
        if (!Enum.IsDefined(TargetPlan))
            TargetPlan = PlanId.PowerSaver;
    }
}

public record ThermalGuardState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("sensorsAvailable")] public bool SensorsAvailable { get; init; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public double? GpuTemp { get; init; }
    [JsonPropertyName("peakTemp")] public double? PeakTemp { get; init; }
    [JsonPropertyName("thresholdCelsius")] public double ThresholdCelsius { get; init; } = 90;
    [JsonPropertyName("coolThresholdCelsius")] public double CoolThresholdCelsius { get; init; } = 82;
    [JsonPropertyName("holdSeconds")] public int HoldSeconds { get; init; } = 20;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.PowerSaver;
    [JsonPropertyName("watchGpu")] public bool WatchGpu { get; init; } = true;
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    [JsonPropertyName("hotHoldSeconds")] public double HotHoldSeconds { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// After the user is idle (no keyboard/mouse input) for a configurable time,
/// switch to a frugal power plan. Restore on activity resume.
/// </summary>
public class IdlePowerGuardSettings
{
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 120;

    // Opt-in: idle plan changes can surprise users who leave long renders running
    // without keep-awake (CPU automation still covers load-based cases).
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("idleMinutes")] public int IdleMinutes { get; set; } = 10;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; set; } = PlanId.PowerSaver;
    /// <summary>When true, only engage while running on battery.</summary>
    [JsonPropertyName("onlyOnBattery")] public bool OnlyOnBattery { get; set; } = true;

    public void Normalize()
    {
        IdleMinutes = Math.Clamp(IdleMinutes, MinIdleMinutes, MaxIdleMinutes);
        if (!Enum.IsDefined(TargetPlan))
            TargetPlan = PlanId.PowerSaver;
    }
}

public record IdlePowerGuardState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("idleMinutes")] public int IdleMinutes { get; init; } = 10;
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.PowerSaver;
    [JsonPropertyName("onlyOnBattery")] public bool OnlyOnBattery { get; init; } = true;
    [JsonPropertyName("idleSeconds")] public double IdleSeconds { get; init; }
    [JsonPropertyName("inputAvailable")] public bool InputAvailable { get; init; } = true;
    [JsonPropertyName("onBattery")] public bool? OnBattery { get; init; }
    [JsonPropertyName("savedPlan")] public PlanId? SavedPlan { get; init; }
    // idle | active | waiting | disabled | battery_skip | no_input | manual_override
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

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
    [JsonPropertyName("widgets")] public WidgetSettings Widgets { get; set; } = new();
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

public record PowerPlan
{
    [JsonPropertyName("planId")] public PlanId? PlanId { get; init; }
    [JsonPropertyName("guid")] public string Guid { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("isActive")] public bool IsActive { get; init; }
}

public record UpdateInfo
{
    [JsonPropertyName("status")] public string Status { get; init; } = "ok"; // ok | offline | ratelimited | norelease | error
    [JsonPropertyName("updateAvailable")] public bool UpdateAvailable { get; init; }
    [JsonPropertyName("latestVersion")] public string? LatestVersion { get; init; }
    [JsonPropertyName("currentVersion")] public string CurrentVersion { get; init; } = "";
    [JsonPropertyName("releaseNotes")] public string? ReleaseNotes { get; init; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("commits")] public List<CommitInfo> Commits { get; init; } = new();
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public record CommitInfo
{
    [JsonPropertyName("sha")] public string Sha { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("date")] public string Date { get; init; } = "";
}

public record ReleaseEntry
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("htmlUrl")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("isCurrent")] public bool IsCurrent { get; init; }
}

public record ReleaseHistory
{
    [JsonPropertyName("status")] public string Status { get; init; } = "ok"; // ok|offline|ratelimited|norelease|error
    [JsonPropertyName("currentVersion")] public string CurrentVersion { get; init; } = "";
    [JsonPropertyName("releases")] public List<ReleaseEntry> Releases { get; init; } = new();
    [JsonPropertyName("commits")] public List<CommitInfo> Commits { get; init; } = new(); // fallback se nessuna release
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>
/// Display/sleep inactivity timeouts for a specific Windows power plan.
/// Values are expressed in seconds; 0 means "never".
/// </summary>
public record PowerPlanTimeoutSet
{
    [JsonPropertyName("planGuid")] public string PlanGuid { get; init; } = "";
    [JsonPropertyName("planName")] public string PlanName { get; init; } = "";
    [JsonPropertyName("displayTimeoutAc")] public int DisplayTimeoutAc { get; init; }
    [JsonPropertyName("displayTimeoutDc")] public int DisplayTimeoutDc { get; init; }
    [JsonPropertyName("sleepTimeoutAc")] public int SleepTimeoutAc { get; init; }
    [JsonPropertyName("sleepTimeoutDc")] public int SleepTimeoutDc { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// Advanced power plan parameters readable/writable via powercfg.
/// AC = alimentazione di rete; DC = batteria.
/// </summary>
public record PlanParameterSet
{
    [JsonPropertyName("planGuid")]   public string PlanGuid { get; init; } = "";
    [JsonPropertyName("planName")]   public string PlanName { get; init; } = "";

    // Processor state 0-100 %
    [JsonPropertyName("processorMinAc")]  public int ProcessorMinAc  { get; init; } = 5;
    [JsonPropertyName("processorMaxAc")]  public int ProcessorMaxAc  { get; init; } = 100;
    [JsonPropertyName("processorMinDc")]  public int ProcessorMinDc  { get; init; } = 5;
    [JsonPropertyName("processorMaxDc")]  public int ProcessorMaxDc  { get; init; } = 100;

    // Processor Performance Boost Mode (Windows-defined indexes 0-6).
    [JsonPropertyName("boostModeAc")] public int BoostModeAc { get; init; } = 2;
    [JsonPropertyName("boostModeDc")] public int BoostModeDc { get; init; } = 2;

    // PCI Express Active State Power Management:
    //   0 = Off, 1 = Moderate Power Saving, 2 = Maximum Power Saving
    [JsonPropertyName("pcieLinkStateAc")] public int PcieLinkStateAc { get; init; } = 0;
    [JsonPropertyName("pcieLinkStateDc")] public int PcieLinkStateDc { get; init; } = 2;

    // Processor Energy Performance Preference: 0 = performance, 100 = efficiency.
    [JsonPropertyName("processorEppAc")] public int ProcessorEppAc { get; init; } = 50;
    [JsonPropertyName("processorEppDc")] public int ProcessorEppDc { get; init; } = 50;
    [JsonPropertyName("processorEppSupported")] public bool ProcessorEppSupported { get; init; }

    // Minimum percentage of logical processors that must remain unparked.
    [JsonPropertyName("coreParkingMinAc")] public int CoreParkingMinAc { get; init; } = 10;
    [JsonPropertyName("coreParkingMinDc")] public int CoreParkingMinDc { get; init; } = 10;
    [JsonPropertyName("coreParkingSupported")] public bool CoreParkingSupported { get; init; }

    // Disk idle timeout in seconds; 0 = never. May be ignored by some storage/Modern Standby systems.
    [JsonPropertyName("diskIdleAc")] public int DiskIdleAc { get; init; }
    [JsonPropertyName("diskIdleDc")] public int DiskIdleDc { get; init; }
    [JsonPropertyName("diskIdleSupported")] public bool DiskIdleSupported { get; init; }

    // Wake timers: 0 = disabled, 1 = enabled, 2 = important timers only.
    [JsonPropertyName("wakeTimersAc")] public int WakeTimersAc { get; init; } = 2;
    [JsonPropertyName("wakeTimersDc")] public int WakeTimersDc { get; init; }
    [JsonPropertyName("wakeTimersSupported")] public bool WakeTimersSupported { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// Capacità batteria lette dal firmware (mWh). Null = nessuna batteria/non leggibile.
/// </summary>
public record BatteryCapacitySnapshot
{
    [JsonPropertyName("designedCapacityMwh")] public int? DesignedCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
}

/// <summary>
/// Stato di salute (usura) della batteria calcolato da capacità progettata vs attuale.
/// </summary>
public record BatteryHealthState
{
    // available=false quando non c'è batteria o i dati firmware sono assenti/invalidi.
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("designedCapacityMwh")] public int? DesignedCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("healthPercent")] public double? HealthPercent { get; init; }
    [JsonPropertyName("wearPercent")] public double? WearPercent { get; init; }
    // excellent|good|fair|poor|unknown
    [JsonPropertyName("rating")] public string Rating { get; init; } = "unknown";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// Lettura istantanea del flusso energetico della batteria dal firmware (WMI BatteryStatus).
/// Tutte le grandezze in unità firmware (mW, mWh, mV). Null = dato assente/non leggibile.
/// </summary>
public record BatteryPowerSnapshot
{
    [JsonPropertyName("powerOnline")] public bool PowerOnline { get; init; }     // alimentatore collegato
    [JsonPropertyName("charging")] public bool Charging { get; init; }
    [JsonPropertyName("discharging")] public bool Discharging { get; init; }
    [JsonPropertyName("chargeRateMw")] public int? ChargeRateMw { get; init; }
    [JsonPropertyName("dischargeRateMw")] public int? DischargeRateMw { get; init; }
    [JsonPropertyName("remainingCapacityMwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("voltageMv")] public int? VoltageMv { get; init; }
}

/// <summary>
/// Stato del flusso energetico della batteria: potenza in carica/scarica (W),
/// percentuale e stima del tempo rimanente (a vuoto o a piena carica).
/// </summary>
public record BatteryPowerState
{
    // available=false quando non c'è batteria o i dati firmware sono assenti.
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("onAc")] public bool OnAc { get; init; }
    // charging|discharging|idle|full|unknown
    [JsonPropertyName("status")] public string Status { get; init; } = "unknown";
    // Potenza con segno: positiva in carica, negativa in scarica (W).
    // Dopo smoothing host: valore stabilizzato (EMA ± mediana storia); istantaneo in InstantPowerWatts.
    [JsonPropertyName("powerWatts")] public double? PowerWatts { get; init; }
    // Lettura firmware grezza prima dello smoothing (null se non applicato).
    [JsonPropertyName("instantPowerWatts")] public double? InstantPowerWatts { get; init; }
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; init; }
    [JsonPropertyName("remainingCapacityMwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("voltageVolts")] public double? VoltageVolts { get; init; }
    // Minuti stimati al traguardo indicato da timeKind.
    [JsonPropertyName("minutesRemaining")] public int? MinutesRemaining { get; init; }
    // toEmpty|toFull|none
    [JsonPropertyName("timeKind")] public string TimeKind { get; init; } = "none";
    // true se potenza/tempo derivano da EMA e/o mediana storica (meno jitter).
    [JsonPropertyName("estimateStable")] public bool EstimateStable { get; init; }
    // Wh scaricati nella sessione a batteria corrente (null se non calcolabile).
    [JsonPropertyName("sessionWh")] public double? SessionWh { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// Campione storico della batteria: istante (epoch secondi UTC), percentuale di carica,
/// potenza con segno (W, + in carica / - in scarica), stato AC e temperatura (°C, opzionale).
/// Chiavi JSON brevi per contenere la dimensione del file di cronologia.
/// </summary>
public record BatteryHistorySample
{
    [JsonPropertyName("t")] public long T { get; init; }
    [JsonPropertyName("pct")] public int? Pct { get; init; }
    [JsonPropertyName("w")] public double? W { get; init; }
    [JsonPropertyName("ac")] public bool Ac { get; init; }
    [JsonPropertyName("temp")] public double? Temp { get; init; }
}

public record ProcessInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("cpuPercent")] public double CpuPercent { get; init; }
    [JsonPropertyName("ramMb")] public double RamMb { get; init; }
    [JsonPropertyName("instances")] public int Instances { get; init; } = 1;
}

/// <summary>
/// Snapshot dettagliato della memoria RAM (in GB e percentuale).
/// </summary>
public record MemoryStatus
{
    [JsonPropertyName("totalGb")]     public double TotalGb     { get; init; }
    [JsonPropertyName("inUseGb")]     public double InUseGb     { get; init; }
    [JsonPropertyName("standbyGb")]   public double StandbyGb   { get; init; }
    [JsonPropertyName("freeGb")]      public double FreeGb      { get; init; }
    [JsonPropertyName("standbyPct")]  public double StandbyPct  { get; init; }
    [JsonPropertyName("inUsePct")]    public double InUsePct    { get; init; }
}
