using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VoltManager.Models;

namespace VoltManager.Services;

public record DetectedHeavyApp
{
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("workingSetMb")] public long WorkingSetMb { get; init; }
}

public record HeavyAppDetectionState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("targetPlan")] public PlanId TargetPlan { get; init; } = PlanId.Performance;
    [JsonPropertyName("detectedCount")] public int DetectedCount { get; init; }
    [JsonPropertyName("activeProcesses")] public List<DetectedHeavyApp> ActiveProcesses { get; init; } = new();
    [JsonPropertyName("lastScanUtc")] public DateTime LastScanUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Detects games and heavy applications without keeping an application catalog.
/// Priority is: Windows Graphics preferences, generic game install locations,
/// then resource fallback for large foreground-class workloads.
/// </summary>
public sealed class HeavyAppDetectionService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog galaxy\games\",
        @"\gog games\",
        @"\xboxgames\",
        @"\riot games\",
        @"\battle.net\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\origin games\",
        @"\.minecraft\",
        @"\itch\apps\",
    };

    private readonly SettingsService _settings;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _scanFaulted; // throttles scan-failure logging to once per streak
    private HeavyAppDetectionState _current = new();

    // Processes already classified as heavy stay tracked by PID across scans, even if their
    // working set later drops below the resource threshold (e.g. when a fullscreen game is
    // alt-tabbed/minimized and Windows trims its memory). A sticky PID is dropped only when the
    // process no longer appears in the enumeration, i.e. it has actually exited.
    private readonly Dictionary<int, DetectedHeavyApp> _sticky = new();

    public event Action<HeavyAppDetectionState>? ActivityChanged;

    public HeavyAppDetectionService(SettingsService settings)
    {
        _settings = settings;
    }

    public HeavyAppDetectionState Current
    {
        get { lock (_lock) return _current; }
    }

    public void Start()
    {
        _timer = new Timer(_ => ScanSafe(), null, TimeSpan.Zero, ScanInterval);
    }

    public HeavyAppDetectionState Refresh()
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
            // Detection must never crash the background automation loop;
            // log the first failure of a streak so a real bug isn't hidden.
            _scanFaulted = Logger.WarnOnce(_scanFaulted, "Heavy-app scan failed", ex);
        }
    }

    private void Scan()
    {
        var config = _settings.Current.HeavyAppDetection ?? new HeavyAppDetectionSettings();
        if (!config.Enabled)
        {
            lock (_lock) _sticky.Clear();
            Publish(new HeavyAppDetectionState
            {
                Enabled = false,
                Active = false,
                TargetPlan = config.TargetPlan,
                LastScanUtc = DateTime.UtcNow,
            });
            return;
        }

        var gpuHighPerformancePaths = config.UseWindowsGpuPreferences
            ? ReadWindowsHighPerformanceGpuPreferences()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var detected = new List<DetectedHeavyApp>();
        var livePids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                livePids.Add(process.Id);

                string path = TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;

                string? reason = Classify(path, process.ProcessName, process.WorkingSet64, gpuHighPerformancePaths, config);
                if (reason == null) continue;

                detected.Add(new DetectedHeavyApp
                {
                    ProcessId = process.Id,
                    Name = string.IsNullOrWhiteSpace(process.ProcessName) ? System.IO.Path.GetFileNameWithoutExtension(path) : process.ProcessName,
                    Path = path,
                    Reason = reason,
                    WorkingSetMb = Math.Max(0, process.WorkingSet64 / 1024 / 1024),
                });
            }
            catch
            {
                // Access can fail for protected/elevated processes; skip them.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Merge with sticky tracking: refresh entries for freshly classified processes, drop
        // sticky PIDs whose process has exited, and keep alive-but-no-longer-qualifying processes
        // (e.g. a minimized game whose working set was trimmed) as still detected.
        lock (_lock)
        {
            foreach (var app in detected)
                _sticky[app.ProcessId] = app;

            foreach (var stalePid in _sticky.Keys.Where(pid => !livePids.Contains(pid)).ToList())
                _sticky.Remove(stalePid);

            var detectedPids = detected.Select(a => a.ProcessId).ToHashSet();

            // ponytail: resourceHeuristic entries don't get sticky — drop them when RAM falls below threshold
            foreach (var pid in _sticky.Keys.Where(pid => !detectedPids.Contains(pid) && _sticky[pid].Reason == "resourceHeuristic").ToList())
                _sticky.Remove(pid);

            foreach (var kept in _sticky.Where(kv => !detectedPids.Contains(kv.Key)))
                detected.Add(kept.Value);
        }

        var unique = detected
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => ReasonPriority(p.Reason)).ThenByDescending(p => p.WorkingSetMb).First())
            .OrderByDescending(p => ReasonPriority(p.Reason))
            .ThenByDescending(p => p.WorkingSetMb)
            .Take(8)
            .ToList();

        Publish(new HeavyAppDetectionState
        {
            Enabled = true,
            Active = unique.Count > 0,
            TargetPlan = config.TargetPlan,
            DetectedCount = detected.Count,
            ActiveProcesses = unique,
            LastScanUtc = DateTime.UtcNow,
        });
    }

    private void Publish(HeavyAppDetectionState next)
    {
        HeavyAppDetectionState previous;
        lock (_lock)
        {
            previous = _current;
            _current = next;
        }

        if (HasMeaningfulChange(previous, next))
            ActivityChanged?.Invoke(next);
    }

    private static bool HasMeaningfulChange(HeavyAppDetectionState previous, HeavyAppDetectionState next)
    {
        if (previous.Enabled != next.Enabled) return true;
        if (previous.Active != next.Active) return true;
        if (previous.TargetPlan != next.TargetPlan) return true;
        if (previous.DetectedCount != next.DetectedCount) return true;

        var prevPaths = previous.ActiveProcesses.Select(p => p.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var nextPaths = next.ActiveProcesses.Select(p => p.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return !prevPaths.SequenceEqual(nextPaths, StringComparer.OrdinalIgnoreCase);
    }

    private static int ReasonPriority(string reason) => reason switch
    {
        "windowsGpuPreference" => 3,
        "gameInstallPath" => 2,
        "resourceHeuristic" => 1,
        _ => 0,
    };

    private static string? Classify(string path, string processName, long workingSetBytes, HashSet<string> gpuHighPerformancePaths, HeavyAppDetectionSettings config)
    {
        string normalized = NormalizePath(path);
        if (config.UseWindowsGpuPreferences && gpuHighPerformancePaths.Contains(normalized))
            return "windowsGpuPreference";

        if (config.UseGameInstallHeuristics && LooksLikeGameInstallPath(normalized))
            return "gameInstallPath";

        if (config.UseResourceHeuristics && LooksLikeHeavyUserProcess(normalized, processName, workingSetBytes, config.MinWorkingSetMb))
            return "resourceHeuristic";

        return null;
    }

    private static bool LooksLikeGameInstallPath(string normalizedPath)
        => GamePathMarkers.Any(marker => normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHeavyUserProcess(string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        if (workingSetBytes / 1024 / 1024 < minWorkingSetMb) return false;
        if (normalizedPath.Contains(@"\windows\", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalizedPath.Contains(@"\microsoft\edge\", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalizedPath.Contains(@"\google\chrome\", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalizedPath.Contains(@"\mozilla firefox\", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

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

    private static HashSet<string> ReadWindowsHighPerformanceGpuPreferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key == null) return paths;

            foreach (string valueName in key.GetValueNames())
            {
                string? value = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!value.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase)) continue;

                paths.Add(NormalizePath(valueName));
            }
        }
        catch
        {
            // Registry may be unavailable or blocked; fall back to path/resource heuristics.
        }
        return paths;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).Trim().Trim('"').ToLowerInvariant();
        }
        catch
        {
            return path.Trim().Trim('"').ToLowerInvariant();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
