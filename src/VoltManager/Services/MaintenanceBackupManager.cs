#pragma warning disable CA1416
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services;

internal sealed class MaintenanceBackupManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private static readonly Regex BackupIdRegex = new(
        @"^[a-zA-Z0-9_\-]+$",
        RegexOptions.Compiled);

    private readonly string _backupRoot;
    private readonly IMaintenanceEnvironment _env;

    public MaintenanceBackupManager(string backupRoot, IMaintenanceEnvironment env)
    {
        _backupRoot = backupRoot;
        _env = env;
    }

    public string CreateBackup(
        string operationKind,
        string description,
        IReadOnlyList<MaintenanceOperationItem> itemsToBackup)
    {
        ArgumentNullException.ThrowIfNull(itemsToBackup);

        if (itemsToBackup.Count == 0)
        {
            throw new ArgumentException("Nessun elemento specificato per il backup.", nameof(itemsToBackup));
        }

        string backupId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        ValidateBackupId(backupId);

        string backupDir = Path.Combine(_backupRoot, backupId);

        try
        {
            if (!_env.IsSafeLocalPath(_backupRoot)) throw new IOException("Unsafe backup root.");
            _env.CreateDirectory(_backupRoot);
            _env.SecureBackupDirectory(_backupRoot);
            _env.CreateDirectory(backupDir);
            _env.SecureBackupDirectory(backupDir);

            string filesDir = Path.Combine(backupDir, "files");
            _env.CreateDirectory(filesDir);

            var manifestItems = new List<BackupManifestItem>();

            foreach (var item in itemsToBackup)
            {
                if (string.Equals(item.Kind, "registry", StringComparison.OrdinalIgnoreCase))
                {
                    var expected = item.RegistryValue ?? throw new InvalidDataException("Missing host registry identity.");
                    string hive = expected.Hive, view = expected.View, subKey = expected.SubKey, valueName = expected.ValueName;
                    var raw = _env.ReadRegistryValue(hive, view, subKey, valueName);
                    if (raw == null || !RegistryEquals(expected, raw)) throw new InvalidDataException("Registry value changed since scan.");
                    string kindStr = raw.ValueKind;
                    string dataBase64 = Convert.ToBase64String(SerializeRegistryValue(raw.ValueData, raw.ValueKind));

                    string hash = ComputeSha256String(Encoding.UTF8.GetBytes(dataBase64));

                    manifestItems.Add(new BackupManifestItem(
                        Id: item.Id,
                        Kind: "registry",
                        OriginalPath: item.Path,
                        RelativePayloadPath: null,
                        Sha256: hash,
                        Reason: item.Reason,
                        RegistryHive: hive,
                        RegistryView: view,
                        RegistrySubKey: subKey,
                        RegistryValueName: valueName,
                        RegistryValueKind: kindStr,
                        RegistryValueDataBase64: dataBase64));
                }
                else
                {
                    // File
                    if (!_env.FileExists(item.Path))
                    {
                        throw new FileNotFoundException($"Impossibile creare il backup: il file '{item.Path}' non esiste più su disco.");
                    }

                    if (item.Kind != "file" || !_env.IsSafeLocalPath(item.Path) || _env.IsProtectedSystemPath(item.Path))
                    {
                        throw new InvalidOperationException($"Percorso di sistema protetto: '{item.Path}'. Impossibile eseguire backup ed eliminazione.");
                    }

                    byte[] fileBytes = _env.ReadFileBytes(item.Path);
                    string sha256 = ComputeSha256String(fileBytes);

                    // Safe payload name without colons or path separators
                    string safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id)))[..16];
                    string payloadName = $"{safeId}_{Path.GetFileName(item.Path)}";
                    string destinationPath = Path.Combine(filesDir, payloadName);

                    _env.CopyFile(item.Path, destinationPath, overwrite: false);
                    if (!string.Equals(sha256, ComputeSha256String(_env.ReadFileBytes(destinationPath)), StringComparison.Ordinal))
                        throw new InvalidDataException("Backup payload verification failed.");
                    if (!string.Equals(sha256, ComputeSha256String(_env.ReadFileBytes(item.Path)), StringComparison.Ordinal))
                        throw new InvalidDataException("Backup source changed during copy.");

                    manifestItems.Add(new BackupManifestItem(
                        Id: item.Id,
                        Kind: item.Kind,
                        OriginalPath: item.Path,
                        RelativePayloadPath: Path.Combine("files", payloadName).Replace('\\', '/'),
                        Sha256: sha256,
                        Reason: item.Reason,
                        RegistryHive: null,
                        RegistryView: null,
                        RegistrySubKey: null,
                        RegistryValueName: null,
                        RegistryValueKind: null,
                        RegistryValueDataBase64: null));
                }
            }

            // Write atomic manifest
            string itemsJson = JsonSerializer.Serialize(manifestItems, JsonOpts);
            string manifestSha256 = ComputeSha256String(Encoding.UTF8.GetBytes(itemsJson));

            var fullManifest = new BackupManifest(
                Version: 1,
                BackupId: backupId,
                CreatedAt: DateTime.UtcNow.ToString("o"),
                Description: description,
                OperationKind: operationKind,
                Items: manifestItems,
                ManifestSha256: manifestSha256);

            string fullJson = JsonSerializer.Serialize(fullManifest, JsonOpts);
            string manifestPath = Path.Combine(backupDir, "manifest.json");
            _env.WriteAllText(manifestPath, fullJson);
            if (_env.ReadAllText(manifestPath) != fullJson) throw new InvalidDataException("Backup manifest verification failed.");

            return backupId;
        }
        catch
        {
            try { _env.DeleteDirectory(backupDir, recursive: true); }
            catch { /* Preserve the original backup failure if partial-archive cleanup also fails. */ }
            throw;
        }
    }

    public IReadOnlyList<MaintenanceBackupSummary> GetBackups()
    {
        var summaries = new List<MaintenanceBackupSummary>();

        if (!_env.DirectoryExists(_backupRoot))
            return summaries;

        try
        {
            var dirInfo = new DirectoryInfo(_backupRoot);
            foreach (var sub in dirInfo.GetDirectories())
            {
                string manifestPath = Path.Combine(sub.FullName, "manifest.json");
                if (!_env.FileExists(manifestPath))
                    continue;

                try
                {
                    string json = _env.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                    if (manifest == null)
                        continue;

                    string itemsJson = JsonSerializer.Serialize(manifest.Items, JsonOpts);
                    string expectedHash = ComputeSha256String(Encoding.UTF8.GetBytes(itemsJson));
                    bool valid = string.Equals(expectedHash, manifest.ManifestSha256, StringComparison.OrdinalIgnoreCase);

                    summaries.Add(new MaintenanceBackupSummary(
                        Id: manifest.BackupId,
                        CreatedAt: manifest.CreatedAt,
                        Description: manifest.Description,
                        ItemCount: manifest.Items.Count,
                        Status: valid ? "active" : "corrupted"));
                }
                catch
                {
                    summaries.Add(new MaintenanceBackupSummary(
                        Id: sub.Name,
                        CreatedAt: sub.CreationTimeUtc.ToString("o"),
                        Description: "Archivio illeggibile o corrotto",
                        ItemCount: 0,
                        Status: "corrupted"));
                }
            }
        }
        catch
        {
            // Directory read error
        }

        summaries.Sort((a, b) => string.Compare(b.CreatedAt, a.CreatedAt, StringComparison.Ordinal));
        return summaries;
    }

    public MaintenanceBackupDetail GetBackup(string backupId)
    {
        ValidateBackupId(backupId);

        string backupDir = Path.Combine(_backupRoot, backupId);
        string manifestPath = Path.Combine(backupDir, "manifest.json");

        if (!_env.FileExists(manifestPath))
        {
            throw new KeyNotFoundException($"Backup '{backupId}' non trovato.");
        }

        string json = _env.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json)
            ?? throw new InvalidDataException("Manifest di backup non valido.");

        string itemsJson = JsonSerializer.Serialize(manifest.Items, JsonOpts);
        string expectedHash = ComputeSha256String(Encoding.UTF8.GetBytes(itemsJson));
        string status = string.Equals(expectedHash, manifest.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            ? "active"
            : "corrupted";

        var detailItems = manifest.Items.Select(i => new MaintenanceBackupItem(
            Id: i.Id,
            Kind: i.Kind,
            Path: i.OriginalPath,
            Reason: i.Reason)).ToList();

        return new MaintenanceBackupDetail(
            Id: manifest.BackupId,
            Description: manifest.Description,
            Items: detailItems,
            Status: status);
    }

    public MaintenanceOperation RestoreBackup(string backupId, IReadOnlyList<string> itemIds)
    {
        ValidateBackupId(backupId);
        ArgumentNullException.ThrowIfNull(itemIds);

        string backupDir = Path.Combine(_backupRoot, backupId);
        string manifestPath = Path.Combine(backupDir, "manifest.json");

        if (!_env.FileExists(manifestPath))
        {
            throw new KeyNotFoundException($"Backup '{backupId}' non trovato.");
        }

        string json = _env.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json)
            ?? throw new InvalidDataException("Manifest di backup non valido.");

        string itemsJson = JsonSerializer.Serialize(manifest.Items, JsonOpts);
        string expectedHash = ComputeSha256String(Encoding.UTF8.GetBytes(itemsJson));
        if (!string.Equals(expectedHash, manifest.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Il backup risulta manomesso o corrotto. Ripristino rifiutato per sicurezza.");
        }

        var targets = itemIds.Count > 0
            ? manifest.Items.Where(i => itemIds.Contains(i.Id, StringComparer.Ordinal)).ToList()
            : manifest.Items.ToList();

        var operationItems = new List<MaintenanceOperationItem>();
        int restoredCount = 0;

        foreach (var item in targets)
        {
            if (string.Equals(item.Kind, "registry", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(item.RegistryHive) ||
                    string.IsNullOrWhiteSpace(item.RegistryView) ||
                    string.IsNullOrWhiteSpace(item.RegistrySubKey))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "Coordinate registro mancanti nel backup",
                        Selectable: false, ContainsUserData: false, Status: "failed"));
                    continue;
                }

                if (_env.ReadRegistryValue(item.RegistryHive, item.RegistryView, item.RegistrySubKey, item.RegistryValueName ?? "") != null)
                {
                    operationItems.Add(new MaintenanceOperationItem(item.Id, item.Kind, item.OriginalPath, "Destination already exists", false, false, "conflict"));
                    continue;
                }
                bool setOk = _env.TrySetRegistryValueIfAbsent(
                    item.RegistryHive,
                    item.RegistryView,
                    item.RegistrySubKey,
                    item.RegistryValueName ?? "",
                    item.RegistryValueKind ?? "String",
                    item.RegistryValueDataBase64 ?? "");

                if (setOk) restoredCount++;

                string status = setOk
                    ? "restored"
                    : _env.ReadRegistryValue(item.RegistryHive, item.RegistryView, item.RegistrySubKey, item.RegistryValueName ?? "") != null
                        ? "conflict"
                        : "failed";
                operationItems.Add(new MaintenanceOperationItem(
                    item.Id, item.Kind, item.OriginalPath, item.Reason,
                    Selectable: false, ContainsUserData: false, Status: status));
            }
            else
            {
                // File restore
                if (string.IsNullOrWhiteSpace(item.RelativePayloadPath))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "Payload relativo non definito",
                        Selectable: false, ContainsUserData: false, Status: "failed"));
                    continue;
                }

                string quarantinedPath = Path.GetFullPath(Path.Combine(backupDir, item.RelativePayloadPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!quarantinedPath.StartsWith(Path.GetFullPath(backupDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !_env.IsSafeLocalPath(quarantinedPath))
                {
                    throw new InvalidOperationException("Percorso payload non valido o tentativo di directory traversal.");
                }

                if (!_env.FileExists(quarantinedPath))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "File di quarantena mancante",
                        Selectable: false, ContainsUserData: false, Status: "failed"));
                    continue;
                }

                // Verify quarantined file hash before restore
                byte[] qBytes = _env.ReadFileBytes(quarantinedPath);
                string qHash = ComputeSha256String(qBytes);
                if (!string.Equals(qHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "File di quarantena corrotto o manomesso",
                        Selectable: false, ContainsUserData: false, Status: "tampered_skipped"));
                    continue;
                }

                // Protect destination path against directory traversal or protected system target
                if (!_env.IsSafeLocalPath(item.OriginalPath) || _env.IsProtectedSystemPath(item.OriginalPath))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "Percorso di ripristino non valido o protetto",
                        Selectable: false, ContainsUserData: false, Status: "failed"));
                    continue;
                }
                if (_env.FileExists(item.OriginalPath) || _env.DirectoryExists(item.OriginalPath))
                {
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, "Destination already exists",
                        Selectable: false, ContainsUserData: false, Status: "conflict"));
                    continue;
                }

                try
                {
                    string? dir = Path.GetDirectoryName(item.OriginalPath);
                    if (!string.IsNullOrWhiteSpace(dir) && !_env.DirectoryExists(dir))
                    {
                        _env.CreateDirectory(dir);
                    }

                    _env.CopyFile(quarantinedPath, item.OriginalPath, overwrite: false);
                    restoredCount++;

                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, item.Reason,
                        Selectable: false, ContainsUserData: false, Status: "restored"));
                }
                catch (Exception ex)
                {
                    bool destinationExists = _env.FileExists(item.OriginalPath) || _env.DirectoryExists(item.OriginalPath);
                    operationItems.Add(new MaintenanceOperationItem(
                        item.Id, item.Kind, item.OriginalPath, $"Errore ripristino: {ex.Message}",
                        Selectable: false, ContainsUserData: false, Status: destinationExists ? "conflict" : "failed"));
                }
            }
        }

        return new MaintenanceOperation(
            Id: Guid.NewGuid().ToString("N"),
            Kind: "restore",
            Status: "completed",
            Message: $"Ripristino completato: {restoredCount}/{targets.Count} elementi ripristinati.",
            Items: operationItems,
            Processed: restoredCount,
            Total: targets.Count,
            BackupId: backupId);
    }

    public bool DeleteBackup(string backupId)
    {
        ValidateBackupId(backupId);

        string backupDir = Path.Combine(_backupRoot, backupId);
        if (!_env.DirectoryExists(backupDir))
            return false;

        _env.DeleteDirectory(backupDir, recursive: true);
        return true;
    }

    private static void ValidateBackupId(string backupId)
    {
        if (string.IsNullOrWhiteSpace(backupId)
            || backupId.Length > 128
            || !BackupIdRegex.IsMatch(backupId)
            || backupId.Contains(".."))
        {
            throw new ArgumentException("Identificativo backup non valido o non sicuro.", nameof(backupId));
        }
    }

    internal static bool RegistryEquals(RawRegistryValueRecord left, RawRegistryValueRecord right)
        => left.Hive == right.Hive && left.View == right.View && left.SubKey == right.SubKey && left.ValueName == right.ValueName
           && left.ValueKind == right.ValueKind && SerializeRegistryValue(left.ValueData, left.ValueKind).SequenceEqual(SerializeRegistryValue(right.ValueData, right.ValueKind));

    public bool MatchesBackup(string backupId, MaintenanceOperationItem item)
    {
        ValidateBackupId(backupId);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(_env.ReadAllText(Path.Combine(_backupRoot, backupId, "manifest.json")))
            ?? throw new InvalidDataException("Missing manifest.");
        if (ComputeSha256String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest.Items, JsonOpts))) != manifest.ManifestSha256)
            throw new InvalidDataException("Invalid manifest.");
        var saved = manifest.Items.Single(i => i.Id == item.Id);
        if (item.RegistryValue is { } registry)
        {
            var current = _env.ReadRegistryValue(registry.Hive, registry.View, registry.SubKey, registry.ValueName);
            return current != null && RegistryEquals(registry, current) && Convert.ToBase64String(SerializeRegistryValue(current.ValueData, current.ValueKind)) == saved.RegistryValueDataBase64;
        }
        if (!_env.IsSafeLocalPath(item.Path)) return false;
        string payload = Path.Combine(_backupRoot, backupId, saved.RelativePayloadPath!);
        return ComputeSha256String(_env.ReadFileBytes(item.Path)) == saved.Sha256
            && ComputeSha256String(_env.ReadFileBytes(payload)) == saved.Sha256;
    }

    private static byte[] SerializeRegistryValue(object? value, string kindStr)
    {
        if (value == null) return Array.Empty<byte>();

        return kindStr switch
        {
            "String" or "ExpandString" =>
                Encoding.Unicode.GetBytes(value.ToString() ?? ""),
            "DWord" =>
                BitConverter.GetBytes(Convert.ToInt32(value)),
            "QWord" =>
                BitConverter.GetBytes(Convert.ToInt64(value)),
            "Binary" =>
                value is byte[] bytes ? bytes : Array.Empty<byte>(),
            "MultiString" =>
                value is string[] strArray
                    ? Encoding.Unicode.GetBytes(string.Join("\0", strArray))
                    : Encoding.Unicode.GetBytes(value.ToString() ?? ""),
            _ => Encoding.Unicode.GetBytes(value.ToString() ?? ""),
        };
    }

    private static string ComputeSha256String(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
