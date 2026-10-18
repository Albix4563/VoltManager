using System.Text.Json.Serialization;

namespace VoltManager.Models;

/// <summary>
/// Installed application detected from Windows registry uninstall keys.
/// </summary>
public sealed record MaintenanceAppEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("publisher")] string? Publisher,
    [property: JsonPropertyName("installLocation")] string? InstallLocation,
    [property: JsonPropertyName("estimatedSizeKb")] long? EstimatedSizeKb,
    [property: JsonPropertyName("canUninstall")] bool CanUninstall,
    [property: JsonPropertyName("reason")] string Reason
);

/// <summary>
/// Result of the applications scan, including optional warnings (e.g. truncation or access warnings).
/// </summary>
public sealed record MaintenanceAppsResult(
    [property: JsonPropertyName("apps")] IReadOnlyList<MaintenanceAppEntry> Apps,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings
);

/// <summary>
/// Single candidate item discovered during registry or leftovers maintenance scan.
/// </summary>
public sealed record MaintenanceOperationItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind, // "file" | "directory" | "registry"
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("selectable")] bool Selectable,
    [property: JsonPropertyName("containsUserData")] bool ContainsUserData,
    [property: JsonPropertyName("status")] string? Status = null // "pending", "cleaned", "skipped", "failed", "restored"
)
{
    [JsonIgnore] internal Services.RawRegistryValueRecord? RegistryValue { get; init; }
    [JsonIgnore] internal string? InstallRoot { get; init; }
}

/// <summary>
/// Lifecycle and progress state of a maintenance operation (scan, uninstall, apply, restore).
/// </summary>
public sealed record MaintenanceOperation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind, // "registry" | "leftovers" | "uninstall" | "apply" | "restore"
    [property: JsonPropertyName("status")] string Status, // "running" | "completed" | "cancelled" | "failed" | "unknown" | "needsVerification" | "rebootRequired"
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("items")] IReadOnlyList<MaintenanceOperationItem> Items,
    [property: JsonPropertyName("processed")] int Processed,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("canScanLeftovers")] bool? CanScanLeftovers = null,
    [property: JsonPropertyName("appId")] string? AppId = null,
    [property: JsonPropertyName("backupId")] string? BackupId = null
);

/// <summary>
/// Summary descriptor of a maintenance backup archive for listing.
/// </summary>
public sealed record MaintenanceBackupSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("itemCount")] int ItemCount,
    [property: JsonPropertyName("status")] string Status // "active" | "restored" | "corrupted"
);

/// <summary>
/// Detail view of an item inside a backup archive.
/// </summary>
public sealed record MaintenanceBackupItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind, // "file" | "directory" | "registry"
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("status")] string? Status = null
);

/// <summary>
/// Detailed inspection of a specific backup archive.
/// </summary>
public sealed record MaintenanceBackupDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("items")] IReadOnlyList<MaintenanceBackupItem> Items,
    [property: JsonPropertyName("status")] string Status
);

/// <summary>
/// Persisted exclusions list to suppress items from future maintenance scans.
/// </summary>
public sealed record MaintenanceExclusions(
    [property: JsonPropertyName("excludedIds")] HashSet<string> ExcludedIds,
    [property: JsonPropertyName("excludedPaths")] HashSet<string> ExcludedPaths
);

/// <summary>
/// Internal tamper-evident manifest record for a backup item.
/// </summary>
public sealed record BackupManifestItem(
    string Id,
    string Kind, // "registry" | "file" | "directory"
    string OriginalPath,
    string? RelativePayloadPath,
    string Sha256,
    string Reason,
    string? RegistryHive,
    string? RegistryView,
    string? RegistrySubKey,
    string? RegistryValueName,
    string? RegistryValueKind,
    string? RegistryValueDataBase64
);

/// <summary>
/// Internal tamper-evident manifest for an entire backup package.
/// </summary>
public sealed record BackupManifest(
    int Version,
    string BackupId,
    string CreatedAt,
    string Description,
    string OperationKind,
    IReadOnlyList<BackupManifestItem> Items,
    string ManifestSha256
);
