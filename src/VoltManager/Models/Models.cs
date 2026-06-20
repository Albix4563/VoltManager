using System.Text.Json.Serialization;

namespace VoltManager.Models;

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
    [JsonPropertyName("hardware")] public string Hardware { get; init; } = "";  // device name
    [JsonPropertyName("category")] public string Category { get; init; } = "";  // cpu|gpu|storage|motherboard
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";          // temp|fan
    [JsonPropertyName("value")] public double Value { get; init; }
}

public record SystemInfo
{
    [JsonPropertyName("cpuName")] public string CpuName { get; init; } = "";
    [JsonPropertyName("gpuName")] public string GpuName { get; init; } = "";
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("osVersion")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("hasBattery")] public bool HasBattery { get; init; }
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
    [JsonPropertyName("action")] public string Action { get; set; } = "shutdown"; // shutdown|restart|sleep
    [JsonPropertyName("time")] public string Time { get; set; } = "23:00";
    [JsonPropertyName("lastTriggeredLocalDate")] public string? LastTriggeredLocalDate { get; set; }
}

public class AutoUpdateSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
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
}

public class PowerSourcePlanSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("pluggedPlan")] public PlanId PluggedPlan { get; set; } = PlanId.Performance;
    [JsonPropertyName("unpluggedMode")] public string UnpluggedMode { get; set; } = "previous";
}

public record KeepAwakeState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("applied")] public bool Applied { get; init; }
    [JsonPropertyName("lastChangedUtc")] public DateTime? LastChangedUtc { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
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

public class AppSettings
{
    [JsonPropertyName("standbyAutoCleaner")] public StandbyAutoCleanerSettings StandbyAutoCleaner { get; set; } = new();
    [JsonPropertyName("theme")] public string Theme { get; set; } = "dark";
    [JsonPropertyName("masterAutomationEnabled")] public bool MasterAutomationEnabled { get; set; } = true;
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; } = true;
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;
    [JsonPropertyName("updateRepo")] public string UpdateRepo { get; set; } = "Albix4563/power_efficency";
    [JsonPropertyName("rules")] public List<AutomationRule> Rules { get; set; } = DefaultRules();
    // Kept as autoShutdown for backwards compatibility with existing settings.json files.
    [JsonPropertyName("autoShutdown")] public AutoShutdownSettings AutoShutdown { get; set; } = new();
    [JsonPropertyName("autoUpdates")] public AutoUpdateSettings AutoUpdates { get; set; } = new();
    [JsonPropertyName("heavyAppDetection")] public HeavyAppDetectionSettings HeavyAppDetection { get; set; } = new();
    [JsonPropertyName("appPowerProfiles")] public AppPowerProfileSettings AppPowerProfiles { get; set; } = new();
    [JsonPropertyName("keepAwake")] public KeepAwakeSettings KeepAwake { get; set; } = new();
    [JsonPropertyName("powerSourcePlan")] public PowerSourcePlanSettings PowerSourcePlan { get; set; } = new();
    // duplicatescheme assigns new GUIDs; map canonical plan -> actual GUID on this machine.
    [JsonPropertyName("planGuidMap")] public Dictionary<string, string> PlanGuidMap { get; set; } = new();
    [JsonPropertyName("override")] public ManualOverride? Override { get; set; }
    [JsonPropertyName("welcomeCompleted")] public bool WelcomeCompleted { get; set; } = false;
    [JsonPropertyName("tourCompleted")] public bool TourCompleted { get; set; } = false;

    public static List<AutomationRule> DefaultRules() => new()
    {
        new AutomationRule { Id = "saver",       Comparison = "lt", ThresholdPct = 10, DurationMinutes = 1, TargetPlan = PlanId.PowerSaver },
        new AutomationRule { Id = "balanced",    Comparison = "gt", ThresholdPct = 10, DurationMinutes = 1, TargetPlan = PlanId.Balanced },
        new AutomationRule { Id = "performance", Comparison = "gt", ThresholdPct = 50, DurationMinutes = 1, TargetPlan = PlanId.Performance },
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

    // Processor Performance Boost Mode:
    //   0 = Disabled, 1 = Enabled, 2 = Aggressive, 4 = Efficient Aggressive
    [JsonPropertyName("boostModeAc")] public int BoostModeAc { get; init; } = 2;
    [JsonPropertyName("boostModeDc")] public int BoostModeDc { get; init; } = 2;

    // PCI Express Active State Power Management:
    //   0 = Off, 1 = Moderate Power Saving, 2 = Maximum Power Saving
    [JsonPropertyName("pcieLinkStateAc")] public int PcieLinkStateAc { get; init; } = 0;
    [JsonPropertyName("pcieLinkStateDc")] public int PcieLinkStateDc { get; init; } = 2;

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
    [JsonPropertyName("powerWatts")] public double? PowerWatts { get; init; }
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; init; }
    [JsonPropertyName("remainingCapacityMwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("voltageVolts")] public double? VoltageVolts { get; init; }
    // Minuti stimati al traguardo indicato da timeKind.
    [JsonPropertyName("minutesRemaining")] public int? MinutesRemaining { get; init; }
    // toEmpty|toFull|none
    [JsonPropertyName("timeKind")] public string TimeKind { get; init; } = "none";
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
