using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoltManager.Services;

namespace VoltManager.Fans;

public sealed record FanProfileImportResult(FanProfile Profile, FanProfileCompatibilityReport Compatibility);

/// <summary>
/// Versioned, data-only fan profile persistence. Files are JSON and are validated before
/// they are stored or exported. No imported field is interpreted as executable content.
/// </summary>
public sealed class FanProfileStore
{
    private const long MaxProfileBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly FanProfileCompatibilityAnalyzer _compatibility = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public FanProfileStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager",
            "FanProfiles");
    }

    public IReadOnlyList<FanProfileSummary> List()
    {
        lock (_gate)
        {
            EnsureDirectory();
            var summaries = new List<FanProfileSummary>();
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = ReadProfile(path);
                    summaries.Add(ToSummary(profile));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Ignoring invalid fan profile '{Path.GetFileName(path)}': {ex.Message}");
                }
            }
            return summaries.OrderByDescending(x => x.ModifiedAtUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public FanProfile Get(string id)
    {
        lock (_gate)
        {
            string path = GetProfilePath(id);
            if (!File.Exists(path)) throw new FileNotFoundException("Fan profile not found.", path);
            return ReadProfile(path);
        }
    }

    public FanProfileSummary Save(FanProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = FanProfileValidator.Validate(profile);
        if (!validation.Valid)
            throw new InvalidDataException(string.Join(" ", validation.Errors));

        lock (_gate)
        {
            EnsureDirectory();
            if (!IsSafeId(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            profile.Name = profile.Name.Trim();
            if (profile.CreatedAtUtc == default) profile.CreatedAtUtc = DateTime.UtcNow;
            profile.ModifiedAtUtc = DateTime.UtcNow;
            AtomicWrite(GetProfilePath(profile.Id), JsonSerializer.Serialize(profile, JsonOptions));
            return ToSummary(profile);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            string path = GetProfilePath(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    public FanProfileImportResult Import(string sourcePath, FanTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Import path is required.", nameof(sourcePath));

        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Fan profile file not found.", sourcePath);
        if (info.Length <= 0 || info.Length > MaxProfileBytes)
            throw new InvalidDataException("Fan profile file is empty or exceeds the 1 MB safety limit.");

        FanProfile profile;
        lock (_gate)
        {
            profile = ReadProfile(sourcePath);
            // Import never overwrites an existing local profile by identifier.
            profile.Id = Guid.NewGuid().ToString("N");
            profile.CreatedAtUtc = DateTime.UtcNow;
            profile.ModifiedAtUtc = DateTime.UtcNow;
            Save(profile);
        }

        return new FanProfileImportResult(profile, _compatibility.Analyze(profile, topology));
    }

    public string Export(string id, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Export path is required.", nameof(destinationPath));

        lock (_gate)
        {
            var profile = Get(id);
            string fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            AtomicWrite(fullPath, JsonSerializer.Serialize(profile, JsonOptions));
            return fullPath;
        }
    }

    public FanProfileCompatibilityReport Analyze(string id, FanTopology topology) =>
        _compatibility.Analyze(Get(id), topology);

    private FanProfile ReadProfile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxProfileBytes)
            throw new InvalidDataException("Fan profile file is empty or too large.");

        string json = File.ReadAllText(path);
        FanProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<FanProfile>(json, JsonOptions)
                ?? throw new InvalidDataException("Fan profile JSON is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Fan profile JSON does not match schema version 1.", ex);
        }
        var validation = FanProfileValidator.Validate(profile);
        if (!validation.Valid)
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        return profile;
    }

    private void EnsureDirectory() => Directory.CreateDirectory(_directory);

    private string GetProfilePath(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("Invalid fan profile identifier.", nameof(id));
        return Path.Combine(_directory, id + ".json");
    }

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static FanProfileSummary ToSummary(FanProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        ModifiedAtUtc = profile.ModifiedAtUtc,
        FanCount = profile.Fans.Count,
    };

    private static void AtomicWrite(string path, string content)
    {
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}

/// <summary>Small persistent store for user-assigned physical fan names.</summary>
public sealed class FanAliasStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public FanAliasStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager",
            "fan-aliases.json");
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_gate)
            return new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
    }

    public void Set(string fanId, string? alias)
    {
        if (string.IsNullOrWhiteSpace(fanId) || fanId.Length > 100)
            throw new ArgumentException("Invalid fan identifier.", nameof(fanId));

        string normalized = (alias ?? "").Trim();
        if (normalized.Length > 60)
            throw new ArgumentException("Fan name cannot exceed 60 characters.", nameof(alias));

        lock (_gate)
        {
            var aliases = Load();
            if (normalized.Length == 0) aliases.Remove(fanId);
            else aliases[fanId] = normalized;

            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            string tempPath = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(aliases, JsonOptions));
                File.Move(tempPath, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), JsonOptions);
                if (parsed != null)
                {
                    _cache = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                    return _cache;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Fan aliases could not be loaded: " + ex.Message);
        }

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }
}
