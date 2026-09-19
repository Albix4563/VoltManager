using System.Text.Json.Serialization;

namespace VoltManager.Models;

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

