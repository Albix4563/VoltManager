using System.Diagnostics;
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

    private readonly SettingsService _settings;
    private readonly object _lock = new();
    private Timer? _timer;
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
        }
        catch
        {
            // App-profile detection must never crash background automation.
        }
    }

    private void Scan()
    {
        var config = _settings.Current.AppPowerProfiles ?? new AppPowerProfileSettings();
        var rules = config.Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path))
            .GroupBy(r => NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(r => NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase);

        if (!config.Enabled || rules.Count == 0)
        {
            Publish(new AppPowerProfileState
            {
                Enabled = config.Enabled,
                Active = false,
                LastScanUtc = DateTime.UtcNow,
            });
            return;
        }

        var detected = new List<DetectedAppPowerProfile>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;

                string path = TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;

                string normalizedPath = NormalizePath(path);
                if (!rules.TryGetValue(normalizedPath, out var rule)) continue;

                detected.Add(new DetectedAppPowerProfile
                {
                    RuleId = rule.Id,
                    ProcessId = process.Id,
                    Name = string.IsNullOrWhiteSpace(rule.Name)
                        ? (string.IsNullOrWhiteSpace(process.ProcessName) ? Path.GetFileNameWithoutExtension(path) : process.ProcessName)
                        : rule.Name,
                    Path = path,
                    TargetPlan = rule.TargetPlan,
                    FileExists = File.Exists(rule.Path),
                });
            }
            catch
            {
                // Protected/elevated processes can deny MainModule; skip them.
            }
            finally
            {
                process.Dispose();
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

    public static string NormalizePath(string path)
        => Environment.ExpandEnvironmentVariables(path ?? "").Trim().Trim('"');

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
