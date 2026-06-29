using System.Diagnostics;
using System.IO;
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
    [JsonPropertyName("startedAtUtc")] public DateTime? StartedAtUtc { get; init; }
}

public record ObservedHeavyProcess(int ProcessId, string Path, DateTime? StartedAtUtc, string Name = "", long WorkingSetMb = 0);

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
        var observed = new List<ObservedHeavyProcess>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;

                string path = TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;
                DateTime? startedAtUtc = TryGetProcessStartTimeUtc(process);
                long workingSetMb = Math.Max(0, process.WorkingSet64 / 1024 / 1024);
                observed.Add(new ObservedHeavyProcess(process.Id, path, startedAtUtc, process.ProcessName, workingSetMb));

                string? reason = ClassifyProcess(path, process.ProcessName, process.WorkingSet64, gpuHighPerformancePaths, config);
                if (reason == null) continue;

                detected.Add(new DetectedHeavyApp
                {
                    ProcessId = process.Id,
                    Name = string.IsNullOrWhiteSpace(process.ProcessName) ? System.IO.Path.GetFileNameWithoutExtension(path) : process.ProcessName,
                    Path = path,
                    Reason = reason,
                    WorkingSetMb = workingSetMb,
                    StartedAtUtc = startedAtUtc,
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
        // sticky entries whose PID now belongs to a different executable, and keep alive-but-
        // no-longer-qualifying real game processes (e.g. minimized after alt-tab) as detected.
        lock (_lock)
        {
            detected = MergeStickyDetections(_sticky, detected, observed, DateTime.UtcNow, config.MinWorkingSetMb);
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

    public static List<DetectedHeavyApp> MergeStickyDetections(IDictionary<int, DetectedHeavyApp> sticky,
        IEnumerable<DetectedHeavyApp> detected, IEnumerable<ObservedHeavyProcess> observed, DateTime nowUtc,
        int minWorkingSetMb = 1536)
    {
        var detectedList = detected.ToList();
        var observedByPid = observed
            .GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var app in detectedList)
            sticky[app.ProcessId] = app;

        var detectedPids = detectedList.Select(a => a.ProcessId).ToHashSet();
        foreach (var pid in sticky.Keys.ToList())
        {
            if (!observedByPid.TryGetValue(pid, out var live) || !SameObservedProcess(sticky[pid], live))
            {
                sticky.Remove(pid);
                continue;
            }

            // Resource-only hits are deliberately non-sticky: once the process drops below the
            // threshold it stops forcing the high-performance plan.
            if (!detectedPids.Contains(pid) && sticky[pid].Reason == "resourceHeuristic")
            {
                sticky.Remove(pid);
                continue;
            }

            if (!detectedPids.Contains(pid) && LooksLikeIdleGameHelper(
                    NormalizePath(live.Path),
                    string.IsNullOrWhiteSpace(live.Name) ? sticky[pid].Name : live.Name,
                    live.WorkingSetMb * 1024 * 1024,
                    minWorkingSetMb))
            {
                sticky.Remove(pid);
            }
        }

        foreach (var kept in sticky.Where(kv => !detectedPids.Contains(kv.Key)))
            detectedList.Add(kept.Value);

        return detectedList;
    }

    private static bool SameObservedProcess(DetectedHeavyApp sticky, ObservedHeavyProcess observed)
    {
        if (!NormalizePath(sticky.Path).Equals(NormalizePath(observed.Path), StringComparison.OrdinalIgnoreCase))
            return false;

        if (sticky.StartedAtUtc != null && observed.StartedAtUtc != null)
        {
            var delta = (sticky.StartedAtUtc.Value - observed.StartedAtUtc.Value).Duration();
            if (delta > TimeSpan.FromSeconds(1))
                return false;
        }

        return true;
    }

    public static string? ClassifyProcess(string path, string processName, long workingSetBytes,
        HashSet<string> gpuHighPerformancePaths, HeavyAppDetectionSettings config)
    {
        string normalized = NormalizePath(path);
        bool idleGameHelper = LooksLikeGameInstallPath(normalized) &&
                              LooksLikeIdleGameHelper(normalized, processName, workingSetBytes, config.MinWorkingSetMb);
        if (idleGameHelper)
            return null;

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

    private static bool LooksLikeIdleGameHelper(string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        if (workingSetBytes / 1024 / 1024 >= minWorkingSetMb) return false;

        string file = Path.GetFileNameWithoutExtension(normalizedPath);
        string combined = (file + " " + processName).ToLowerInvariant();
        string[] helperMarkers =
        {
            "launcher", "updater", "update", "bootstrapper", "crashhandler", "crashreporter",
            "reporter", "webhelper", "helper", "service", "setup", "uninstall", "redist", "vc_redist"
        };
        return helperMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

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

    private static DateTime? TryGetProcessStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
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

    public static string NormalizePath(string path)
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
