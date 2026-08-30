using System.IO;
using System.Text.Json.Serialization;
using VoltManager.Models;

namespace VoltManager.Services;

public record DetectedAppPowerProfile
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.Performance;
    [JsonPropertyName("fileExists")] public bool FileExists { get; init; }
}

public record AppPowerProfileState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("targetPlan")] public PlanId? TargetPlan { get; init; }
    [JsonPropertyName("detectedCount")] public int DetectedCount { get; init; }
    [JsonPropertyName("activeProfiles")] public List<DetectedAppPowerProfile> ActiveProfiles { get; init; } = new();
    [JsonPropertyName("lastScanUtc")] public DateTime LastScanUtc { get; init; } = DateTime.UtcNow;
}

public sealed class AppPowerProfileService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    // Same window as the heavy-app scanner: both loops ride one shared enumeration.
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(4);

    private readonly SettingsService _settings;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _scanFaulted; // throttles scan-failure logging to once per streak
    private AppPowerProfileState _current = new();

    public event Action<AppPowerProfileState>? ActivityChanged;

    public AppPowerProfileService(SettingsService settings)
    {
        _settings = settings;
    }

    public AppPowerProfileState Current
    {
        get { lock (_lock) return _current; }
    }

    public void Start()
    {
        _timer = new Timer(_ => ScanSafe(), null, TimeSpan.Zero, ScanInterval);
    }

    /// <summary>Starts detection after <paramref name="delay"/> to avoid
    /// competing with other startup work for process handles and WMI.</summary>
    public void StartDelayed(TimeSpan delay)
    {
        _timer = new Timer(_ => ScanSafe(), null, delay, ScanInterval);
    }

    public AppPowerProfileState Refresh()
    {
        ScanSafe();
        return Current;
    }

    private void ScanSafe()
    {
        try
        {
            Scan();
            _scanFaulted = false;
        }
        catch (Exception ex)
        {
            // App-profile detection must never crash background automation;
            // log the first failure of a streak so a real bug isn't hidden.
            _scanFaulted = Logger.WarnOnce(_scanFaulted, "App-profile scan failed", ex);
        }
    }

    private void Scan()
    {
        var config = _settings.Current.AppPowerProfiles ?? new AppPowerProfileSettings();
        var enabledRules = config.Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path))
            .ToList();

        // Full path wins; exe-name fallback covers moved installs / different drive letters
        // when the user saved only the executable identity.
        var rulesByPath = enabledRules
            .GroupBy(r => NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rulesByFileName = enabledRules
            .GroupBy(r => Path.GetFileName(NormalizePath(r.Path)), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (!config.Enabled || rulesByPath.Count == 0)
        {
            Publish(new AppPowerProfileState
            {
                Enabled = config.Enabled,
                Active = false,
                LastScanUtc = DateTime.UtcNow,
            });
            return;
        }

        var snapshot = ProcessSnapshotProvider.Get(SnapshotMaxAge);
        var detected = new List<DetectedAppPowerProfile>();
        foreach (var process in snapshot.Processes)
        {
            try
            {
                if (process.Pid == Environment.ProcessId) continue;
                if (!CouldMatchProcessName(process.Name, rulesByFileName)) continue;

                string path = ProcessSnapshotProvider.GetPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;

                string normalizedPath = NormalizePath(path);
                if (!TryMatchRule(normalizedPath, process.Name, rulesByPath, rulesByFileName, out var rule))
                    continue;

                detected.Add(new DetectedAppPowerProfile
                {
                    RuleId = rule.Id,
                    ProcessId = process.Pid,
                    Name = string.IsNullOrWhiteSpace(rule.Name)
                        ? (string.IsNullOrWhiteSpace(process.Name) ? Path.GetFileNameWithoutExtension(path) : process.Name)
                        : rule.Name,
                    Path = path,
                    TargetPlan = rule.TargetPlan,
                    FileExists = File.Exists(rule.Path) || File.Exists(path),
                });
            }
            catch
            {
                // Protected/elevated processes can deny the image path; skip them.
            }
        }

        var unique = detected
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => PlanPriority(p.TargetPlan)).First())
            .OrderByDescending(p => PlanPriority(p.TargetPlan))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();

        Publish(new AppPowerProfileState
        {
            Enabled = config.Enabled,
            Active = unique.Count > 0,
            TargetPlan = unique.Count == 0 ? null : unique.OrderByDescending(p => PlanPriority(p.TargetPlan)).First().TargetPlan,
            DetectedCount = detected.Count,
            ActiveProfiles = unique,
            LastScanUtc = DateTime.UtcNow,
        });
    }

    public static bool TryMatchRule(
        string normalizedProcessPath,
        string processName,
        IReadOnlyDictionary<string, AppPowerProfileRule> rulesByPath,
        IReadOnlyDictionary<string, AppPowerProfileRule> rulesByFileName,
        out AppPowerProfileRule rule)
    {
        if (rulesByPath.TryGetValue(normalizedProcessPath, out rule!))
            return true;

        string fileName = Path.GetFileName(normalizedProcessPath);
        if (!string.IsNullOrWhiteSpace(fileName) && rulesByFileName.TryGetValue(fileName, out rule!))
            return true;

        // ProcessName without extension vs rule exe stem (e.g. "code" vs "Code.exe").
        if (!string.IsNullOrWhiteSpace(processName))
        {
            string stem = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";
            if (rulesByFileName.TryGetValue(stem, out rule!))
                return true;
        }

        rule = null!;
        return false;
    }

    internal static bool CouldMatchProcessName(
        string processName,
        IReadOnlyDictionary<string, AppPowerProfileRule> rulesByFileName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;

        string fileName = Path.GetFileName(processName);
        if (rulesByFileName.ContainsKey(fileName)) return true;

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            rulesByFileName.ContainsKey(fileName + ".exe"))
            return true;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        return rulesByFileName.ContainsKey(stem);
    }

    private void Publish(AppPowerProfileState next)
    {
        AppPowerProfileState previous;
        lock (_lock)
        {
            previous = _current;
            _current = next;
        }

        if (HasMeaningfulChange(previous, next))
            ActivityChanged?.Invoke(next);
    }

    private static bool HasMeaningfulChange(AppPowerProfileState previous, AppPowerProfileState next)
    {
        if (previous.Enabled != next.Enabled) return true;
        if (previous.Active != next.Active) return true;
        if (previous.TargetPlan != next.TargetPlan) return true;
        if (previous.DetectedCount != next.DetectedCount) return true;

        var prevIds = previous.ActiveProfiles.Select(p => p.RuleId).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var nextIds = next.ActiveProfiles.Select(p => p.RuleId).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return !prevIds.SequenceEqual(nextIds, StringComparer.OrdinalIgnoreCase);
    }

    public static PlanId? PickTargetPlan(IEnumerable<AppPowerProfileRule> activeRules)
        => activeRules
            .Where(r => r.Enabled)
            .OrderByDescending(r => PlanPriority(r.TargetPlan))
            .Select(r => (PlanId?)r.TargetPlan)
            .FirstOrDefault();

    public static int PlanPriority(PlanId plan) => plan switch
    {
        PlanId.Performance => 3,
        PlanId.Balanced => 2,
        PlanId.PowerSaver => 1,
        _ => 0,
    };

    public static string NormalizePath(string path) => ProcessPathResolver.Normalize(path);

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
