using System.IO;
using System.Text.Json;
using VoltManager.Services;

namespace VoltManager.Fans;

internal sealed record FanControlRecoveryEntry
{
    public string FanId { get; init; } = "";
    public string ControlIdentifier { get; init; } = "";
    public string Backend { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

internal sealed record FanControlRecoveryDocument
{
    public int Version { get; init; } = 1;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public List<FanControlRecoveryEntry> Entries { get; init; } = new();
}

/// <summary>
/// Persists only the identifiers needed to release a software fan-control lease
/// after an unclean shutdown. It never stores arbitrary commands or executable data.
/// </summary>
internal sealed class FanControlRecoveryStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static FanControlRecoveryStore Disabled { get; } = new(null);

    public FanControlRecoveryStore(string? path = "__default__")
    {
        _path = path == "__default__"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoltManager", "fan-control-recovery.json")
            : path;
    }

    public List<FanControlRecoveryEntry> Load()
    {
        if (_path == null) return new();
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new();
                var info = new FileInfo(_path);
                if (info.Length <= 0 || info.Length > 256 * 1024)
                {
                    TryDelete();
                    return new();
                }

                FanControlRecoveryDocument? doc = JsonSerializer.Deserialize<FanControlRecoveryDocument>(File.ReadAllText(_path), JsonOptions);
                if (doc?.Version != 1 || doc.Entries.Count > 128)
                {
                    TryDelete();
                    return new();
                }

                return doc.Entries
                    .Where(IsValid)
                    .GroupBy(x => x.ControlIdentifier, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Last())
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("Fan recovery lease could not be read: " + ex.Message);
                return new();
            }
        }
    }

    public void Save(IEnumerable<FanControlRecoveryEntry> entries)
    {
        if (_path == null) return;
        lock (_gate)
        {
            try
            {
                List<FanControlRecoveryEntry> safe = entries.Where(IsValid)
                    .GroupBy(x => x.ControlIdentifier, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Last())
                    .Take(128)
                    .ToList();
                if (safe.Count == 0)
                {
                    TryDelete();
                    return;
                }

                string directory = Path.GetDirectoryName(_path) ?? ".";
                Directory.CreateDirectory(directory);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new FanControlRecoveryDocument
                {
                    UpdatedAtUtc = DateTime.UtcNow,
                    Entries = safe,
                }, JsonOptions));
                File.Move(temp, _path, true);
            }
            catch (Exception ex)
            {
                Logger.Warn("Fan recovery lease could not be saved: " + ex.Message);
            }
        }
    }

    private static bool IsValid(FanControlRecoveryEntry entry) =>
        entry != null &&
        !string.IsNullOrWhiteSpace(entry.FanId) && entry.FanId.Length <= 160 &&
        !string.IsNullOrWhiteSpace(entry.ControlIdentifier) && entry.ControlIdentifier.Length <= 512 &&
        !string.IsNullOrWhiteSpace(entry.Backend) && entry.Backend.Length <= 80 &&
        entry.DisplayName.Length <= 160;

    private void TryDelete()
    {
        if (_path == null) return;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }
}
