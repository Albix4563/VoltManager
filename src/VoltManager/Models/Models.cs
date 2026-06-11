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
}

public record SystemInfo
{
    [JsonPropertyName("cpuName")] public string CpuName { get; init; } = "";
    [JsonPropertyName("gpuName")] public string GpuName { get; init; } = "";
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("osVersion")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
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

public class AppSettings
{
    [JsonPropertyName("masterAutomationEnabled")] public bool MasterAutomationEnabled { get; set; } = true;
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; } = true;
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;
    [JsonPropertyName("updateRepo")] public string UpdateRepo { get; set; } = "Albix4563/power_efficency";
    [JsonPropertyName("rules")] public List<AutomationRule> Rules { get; set; } = DefaultRules();
    // duplicatescheme assigns new GUIDs; map canonical plan -> actual GUID on this machine.
    [JsonPropertyName("planGuidMap")] public Dictionary<string, string> PlanGuidMap { get; set; } = new();
    [JsonPropertyName("override")] public ManualOverride? Override { get; set; }

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
