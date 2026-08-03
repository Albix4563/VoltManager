using System.IO;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VoltManager.Models;
using VoltManager.Services.GameDetection;

namespace VoltManager.Services;

public record DetectedHeavyApp
{
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("workingSetMb")] public long WorkingSetMb { get; init; }
    [JsonPropertyName("startedAtUtc")] public DateTime? StartedAtUtc { get; init; }
    [JsonPropertyName("confidenceScore")] public int ConfidenceScore { get; init; }
    [JsonPropertyName("confidenceLevel")] public string ConfidenceLevel { get; init; } = "ignored";
    [JsonPropertyName("evidence")] public IReadOnlyList<GameDetectionEvidence> Evidence { get; init; } = Array.Empty<GameDetectionEvidence>();
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
/// Priority: Windows Graphics preferences, install/layout heuristics, then
/// resource fallback for large user workloads.
/// </summary>
public sealed class HeavyAppDetectionService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    // Accepts a snapshot captured by any other scanner within this window, so the
    // three loops normally share a single system-wide enumeration.
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(4);

    // Game content roots only (substring match on normalized path).
    // Do NOT list storefront/client folders here — those keep the high-performance plan
    // stuck on after a session (e.g. EA Desktop next to FIFA / EA FC).
    private static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\",
        @"\steamapps\sourcemods\",
        @"\epic games\",
        @"\legendary\",
        @"\gog galaxy\games\",
        @"\gog games\",
        @"\xboxgames\",
        @"\xbox games\",
        @"\riot games\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\origin games\",
        @"\.minecraft\",
        @"\itch\apps\",
        @"\amazon games\library\",
        @"\rockstar games\",
        @"\bethesda.net\",
        @"\square enix\",
        @"\bandai namco\",
        @"\paradox interactive\",
        @"\2k games\",
        @"\wargaming.net\",
        @"\roblox\",
        @"\oculus\software\",
        @"\meta quest\",
        @"\mihoyo\",
        @"\hoyoverse\",
        @"\genshin impact\",
        @"\honkai\",
        @"\netease\",
        @"\garena\",
        @"\microsoft games\",
        // ponytail: no bare \windowsapps\ — UWP shells (Netflix/Spotify) live there; Xbox uses \xboxgames\
    };

    // Engine / shipping binary layouts common to Unreal, many AAA, etc.
    // Blizzard channel folders (\_retail_\, \_classic_\, …) are install-layout signals for
    // Battle.net titles living outside any storefront path marker — sticky without RAM peak.
    private static readonly string[] GameBinaryLayouts =
    {
        @"\binaries\win64\",
        @"\binaries\win32\",
        @"\bin\win64\",
        @"\bin\win32\",
        @"\engine\binaries\win64\",
        @"\engine\binaries\win32\",
        @"\game\bin\",
        @"\shipping\",
        @"\_retail_\",
        @"\_classic_\",
        @"\_classic_era_\",
        @"\_classic_ptr_\",
        @"\_ptr_\",
        @"\_beta_\",
        @"\_vendor_\",
    };

    // Paths that look like games but are storefront/system shells we never treat as heavy.
    // Checked before GamePathMarkers so launcher roots under shared prefixes (Epic, Rockstar,
    // Riot, …) never force the performance plan while idling in the background.
    private static readonly string[] NonGamePathMarkers =
    {
        @"\windowsapps\microsoft.",
        @"\windowsapps\microsoftwindows.",
        @"\windowsapps\microsoft.windows",
        @"\windowsapps\microsoft.bing",
        @"\windowsapps\microsoft.office",
        @"\windowsapps\microsoft.skypeapp",
        @"\windowsapps\microsoft.zune",
        @"\windowsapps\microsoft.yourphone",
        @"\windowsapps\microsoft.gamingapp",
        @"\windowsapps\microsoft.xbox",
        @"\edgewebview\",
        @"\edge\application\",
        // Storefront clients / helpers (not playable game content)
        @"\ea desktop\",
        @"\electronic arts\ea desktop\",
        @"\origin\origin.exe",
        @"\origin\originwebhelperservice",
        @"\epic games\launcher\",
        @"\epic games\epic games launcher\",
        @"\battle.net\",
        @"\blizzard entertainment\battle.net\",
        @"\riot games\riot client\",
        @"\riot games\league of legends\leagueclient",
        @"\riot games\league of legends\riot client",
        @"\rockstar games\launcher\",
        @"\rockstar games\social club\",
        @"\ubisoft connect\",
        @"\ubisoft game launcher\upc.exe",
        @"\ubisoft game launcher\ubisoftconnect",
        @"\gog galaxy\galaxyclient",
        @"\steam\bin\",
        @"\steam\steam.exe",
        @"\steam\steamapps\common\steamworks shared\",
        @"\amazon games\app\",
        @"\itch\butler",
    };

    private static readonly string[] ResourceDenyPathMarkers =
    {
        @"\windows\",
        @"\microsoft\edge\",
        @"\google\chrome\",
        @"\microsoft\edgewebview\",
        @"\mozilla firefox\",
        @"\brave software\",
        @"\vivaldi\",
        @"\opera\",
        @"\chromium\",
        @"\msedge.exe",
        @"\teams\",
        @"\microsoft teams\",
        @"\slack\",
        @"\zoom\",
        @"\discord\",
        @"\spotify\",
        @"\dropbox\",
        @"\onedrive\",
        @"\code.exe",
        @"\devenv.exe",
        @"\jetbrains\",
    };

    private static readonly string[] ResourceDenyProcessNames =
    {
        "explorer", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "runtimebroker", "sihost", "taskhostw", "dwm", "csrss", "lsass", "services",
        "svchost", "system", "registry", "smss", "winlogon", "fontdrvhost",
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
        "teams", "ms-teams", "slack", "zoom", "discord", "spotify",
        "code", "devenv", "voltmanager",
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

#if DEBUG
    static HeavyAppDetectionService() => RunSelfCheck();
#endif

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

    /// <summary>Starts detection after <paramref name="delay"/> to avoid
    /// competing with other startup work for process handles and WMI.</summary>
    public void StartDelayed(TimeSpan delay)
    {
        _timer = new Timer(_ => ScanSafe(), null, delay, ScanInterval);
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

        var snapshot = ProcessSnapshotProvider.Get(SnapshotMaxAge);
        var processGraph = new ProcessGraph(snapshot.Processes);
        int? foregroundPid = ForegroundProcessProbe.TryGetForegroundProcessId();
        DateTime scanNowUtc = DateTime.UtcNow;
        var detected = new List<DetectedHeavyApp>();
        var observed = new List<ObservedHeavyProcess>();
        foreach (var process in snapshot.Processes)
        {
            try
            {
                if (process.Pid == Environment.ProcessId) continue;

                string path = ProcessSnapshotProvider.GetPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;
                DateTime? startedAtUtc = process.StartTimeUtc;
                long workingSetMb = Math.Max(0, process.WorkingSetBytes / 1024 / 1024);
                observed.Add(new ObservedHeavyProcess(process.Pid, path, startedAtUtc, process.Name, workingSetMb));

                bool hasLauncherAncestor = processGraph.TryFindAncestor(
                    process.Pid,
                    IsLauncherAncestor,
                    maxDepth: 3,
                    out _);
                bool isForeground = foregroundPid != null && foregroundPid.Value == process.Pid;

                // Classify after ancestry/foreground so custom-folder titles can stick without
                // depending only on path markers or peak working-set.
                string? reason = ClassifyProcess(
                    path,
                    process.Name,
                    process.WorkingSetBytes,
                    gpuHighPerformancePaths,
                    config,
                    hasLauncherAncestor,
                    isForeground);
                if (reason == null) continue;

                var assessment = AssessProcess(
                    path,
                    process.Name,
                    process.WorkingSetBytes,
                    gpuHighPerformancePaths,
                    config,
                    startedAtUtc,
                    scanNowUtc,
                    hasLauncherAncestor,
                    isForeground);
                if (assessment.PrimaryReason == null) continue;

                detected.Add(new DetectedHeavyApp
                {
                    ProcessId = process.Pid,
                    Name = string.IsNullOrWhiteSpace(process.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : process.Name,
                    Path = path,
                    Reason = assessment.PrimaryReason,
                    WorkingSetMb = workingSetMb,
                    StartedAtUtc = startedAtUtc,
                    ConfidenceScore = assessment.Score,
                    ConfidenceLevel = assessment.Level,
                    Evidence = assessment.Evidence,
                });
            }
            catch
            {
                // Access can fail for protected/elevated processes; skip them.
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

    public static bool HasMeaningfulChange(HeavyAppDetectionState previous, HeavyAppDetectionState next)
    {
        if (previous.Enabled != next.Enabled) return true;
        if (previous.Active != next.Active) return true;
        if (previous.TargetPlan != next.TargetPlan) return true;
        if (previous.DetectedCount != next.DetectedCount) return true;

        var previousProcesses = previous.ActiveProcesses
            .OrderBy(process => process.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nextProcesses = next.ActiveProcesses
            .OrderBy(process => process.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (previousProcesses.Length != nextProcesses.Length)
            return true;

        for (int index = 0; index < previousProcesses.Length; index++)
        {
            var left = previousProcesses[index];
            var right = nextProcesses[index];
            if (!left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase) ||
                !left.Reason.Equals(right.Reason, StringComparison.Ordinal) ||
                left.ConfidenceScore != right.ConfidenceScore ||
                !left.ConfidenceLevel.Equals(right.ConfidenceLevel, StringComparison.Ordinal) ||
                !left.Evidence.SequenceEqual(right.Evidence))
                return true;
        }

        return false;
    }

    private static int ReasonPriority(string reason) => reason switch
    {
        "windowsGpuPreference" => 4,
        "gameInstallPath" => 3,
        "gameBinaryLayout" => 2,
        "launcherChild" => 2,
        "foregroundActive" => 2,
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
                // Bootstrap/launcher PID often exits after spawning the real game binary under
                // the same install root. Hand the sticky session to that successor so the
                // performance plan does not drop during the handoff (grace alone is short).
                var exiting = sticky[pid];
                sticky.Remove(pid);
                if (exiting.Reason != "resourceHeuristic" &&
                    TryHandoffStickyToInstallRootPeer(
                        sticky, exiting, observedByPid, detectedList, detectedPids, minWorkingSetMb))
                {
                    // Successor may have been appended to detectedList; keep set current.
                    detectedPids = detectedList.Select(a => a.ProcessId).ToHashSet();
                }
                continue;
            }

            if (detectedPids.Contains(pid))
                continue;

            string livePath = NormalizePath(live.Path);
            string liveName = string.IsNullOrWhiteSpace(live.Name) ? sticky[pid].Name : live.Name;

            // Resource-only hits are deliberately non-sticky: once the process drops below the
            // threshold it stops forcing the high-performance plan.
            if (sticky[pid].Reason == "resourceHeuristic")
            {
                sticky.Remove(pid);
                continue;
            }

            // Drop storefront shells / launchers that were sticky from a prior misclassification
            // (e.g. EA Desktop under a broad path marker while FIFA was running).
            if (IsNonGameShell(livePath, liveName) ||
                LooksLikeIdleGameHelper(livePath, liveName, live.WorkingSetMb * 1024 * 1024, minWorkingSetMb))
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

    /// <summary>
    /// When a sticky game PID exits, keep the session if another live non-helper process
    /// remains under the same install root (bootstrap → shipping binary handoff).
    /// </summary>
    private static bool TryHandoffStickyToInstallRootPeer(
        IDictionary<int, DetectedHeavyApp> sticky,
        DetectedHeavyApp exiting,
        IReadOnlyDictionary<int, ObservedHeavyProcess> observedByPid,
        List<DetectedHeavyApp> detectedList,
        HashSet<int> detectedPids,
        int minWorkingSetMb)
    {
        string? root = TryGetGameInstallRoot(NormalizePath(exiting.Path));
        if (string.IsNullOrEmpty(root))
            return false;

        // Already covered by a freshly classified peer under the same root.
        if (detectedList.Any(d =>
                d.ProcessId != exiting.ProcessId &&
                IsPathUnderInstallRoot(NormalizePath(d.Path), root)))
            return false;

        ObservedHeavyProcess? best = null;
        foreach (var live in observedByPid.Values)
        {
            if (live.ProcessId == exiting.ProcessId)
                continue;
            if (sticky.ContainsKey(live.ProcessId) || detectedPids.Contains(live.ProcessId))
                continue;

            string livePath = NormalizePath(live.Path);
            if (!IsPathUnderInstallRoot(livePath, root))
                continue;

            string liveName = string.IsNullOrWhiteSpace(live.Name)
                ? Path.GetFileNameWithoutExtension(live.Path)
                : live.Name;
            if (IsNonGameShell(livePath, liveName) ||
                LooksLikeIdleGameHelper(livePath, liveName, live.WorkingSetMb * 1024 * 1024, minWorkingSetMb))
                continue;

            // Generic parent-dir roots can host unrelated apps; only hand off to peers that
            // look like the game (shipping name) or started with/after the sticky session.
            bool markerRoot = root.Length > 3 && (
                LooksLikeGameInstallPath(root + "\\") ||
                LooksLikeGameBinaryLayout(livePath));
            if (!markerRoot)
            {
                if (!LooksLikeShippingGameBinary(livePath, liveName) &&
                    !StartedWithOrAfter(live.StartedAtUtc, exiting.StartedAtUtc))
                    continue;
            }

            if (best == null || live.WorkingSetMb > best.WorkingSetMb)
                best = live;
        }

        if (best == null)
            return false;

        string bestName = string.IsNullOrWhiteSpace(best.Name)
            ? Path.GetFileNameWithoutExtension(best.Path)
            : best.Name;
        var handed = new DetectedHeavyApp
        {
            ProcessId = best.ProcessId,
            Name = bestName,
            Path = best.Path,
            Reason = exiting.Reason,
            WorkingSetMb = best.WorkingSetMb,
            StartedAtUtc = best.StartedAtUtc,
            ConfidenceScore = exiting.ConfidenceScore,
            ConfidenceLevel = exiting.ConfidenceLevel,
            Evidence = exiting.Evidence,
        };
        sticky[handed.ProcessId] = handed;
        detectedList.Add(handed);
        return true;
    }

    private static bool StartedWithOrAfter(DateTime? candidateStart, DateTime? sessionStart)
    {
        if (candidateStart == null || sessionStart == null)
            return true;
        // Allow a few seconds of clock/scan skew: child may appear slightly "before" parent stamp.
        return candidateStart.Value >= sessionStart.Value.AddSeconds(-5);
    }

    /// <summary>
    /// Best-effort install root for sticky handoff. Prefers storefront content folders and
    /// engine/channel layouts; falls back to the executable's parent directory.
    /// </summary>
    public static string? TryGetGameInstallRoot(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        string path = NormalizePath(normalizedPath);

        foreach (string marker in GamePathMarkers)
        {
            int idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            int contentStart = idx + marker.Length;
            if (contentStart >= path.Length)
                return path[..(idx + marker.Length - 1)];

            int slash = path.IndexOf('\\', contentStart);
            int end = slash < 0 ? path.Length : slash;
            // Include the first folder under the marker (game title directory).
            return path[..end];
        }

        foreach (string layout in GameBinaryLayouts)
        {
            int idx = path.IndexOf(layout, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return path[..idx];
        }

        string? dir = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    private static bool IsPathUnderInstallRoot(string normalizedPath, string installRoot)
    {
        if (string.IsNullOrEmpty(installRoot))
            return false;
        if (normalizedPath.Equals(installRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = installRoot.EndsWith("\\", StringComparison.Ordinal) ? installRoot : installRoot + "\\";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ClassifyProcess(string path, string processName, long workingSetBytes,
        HashSet<string> gpuHighPerformancePaths, HeavyAppDetectionSettings config,
        bool hasLauncherAncestor = false, bool isForeground = false)
        => ClassifyNormalizedProcess(
            NormalizePath(path), processName, workingSetBytes, gpuHighPerformancePaths, config,
            hasLauncherAncestor, isForeground);

    public static GameDetectionAssessment AssessProcess(
        string path,
        string processName,
        long workingSetBytes,
        HashSet<string> gpuHighPerformancePaths,
        HeavyAppDetectionSettings config,
        DateTime? startedAtUtc = null,
        DateTime? nowUtc = null,
        bool hasLauncherAncestor = false,
        bool isForeground = false)
    {
        string normalized = NormalizePath(path);
        string? primaryReason = ClassifyNormalizedProcess(
            normalized, processName, workingSetBytes, gpuHighPerformancePaths, config,
            hasLauncherAncestor, isForeground);
        if (primaryReason == null)
            return GameDetectionAssessment.Empty;

        var evidence = new List<GameDetectionEvidence>(9);

        if (config.UseWindowsGpuPreferences && gpuHighPerformancePaths.Contains(normalized))
            evidence.Add(new GameDetectionEvidence(
                "windowsGpuPreference", "identity", 25, "Windows high-performance GPU preference"));

        if (config.UseGameInstallHeuristics && LooksLikeGameInstallPath(normalized))
            evidence.Add(new GameDetectionEvidence(
                "gameInstallPath", "provenance", 30, "Known game installation path"));

        if (config.UseGameInstallHeuristics && LooksLikeGameBinaryLayout(normalized))
            evidence.Add(new GameDetectionEvidence(
                "gameBinaryLayout", "identity", 20, "Common game binary layout"));

        if (primaryReason == "launcherChild" ||
            (hasLauncherAncestor && !IsNonGameShell(normalized, processName)))
            evidence.Add(new GameDetectionEvidence(
                "launcherAncestry", "provenance", 15, "Known launcher ancestor"));

        if (primaryReason == "foregroundActive" ||
            (isForeground && !IsNonGameShell(normalized, processName)))
            evidence.Add(new GameDetectionEvidence(
                "foreground", "runtime", 20, "Owns the foreground window"));

        if (config.UseResourceHeuristics && LooksLikeHeavyUserProcess(normalized, processName, workingSetBytes, config.MinWorkingSetMb))
            evidence.Add(new GameDetectionEvidence(
                "resourceHeuristic", "runtime", 5, "Heavy working-set fallback"));

        if (startedAtUtc != null && nowUtc != null && nowUtc >= startedAtUtc)
        {
            TimeSpan duration = nowUtc.Value - startedAtUtc.Value;
            if (duration >= TimeSpan.FromSeconds(15))
                evidence.Add(new GameDetectionEvidence(
                    "duration15s", "runtime", 4, "Running for at least 15 seconds"));
            if (duration >= TimeSpan.FromMinutes(2))
                evidence.Add(new GameDetectionEvidence(
                    "duration2m", "runtime", 3, "Running for at least two minutes"));
        }

        return GameConfidenceScorer.Score(evidence, primaryReason);
    }

    private static string? ClassifyNormalizedProcess(
        string normalizedPath,
        string processName,
        long workingSetBytes,
        HashSet<string> gpuHighPerformancePaths,
        HeavyAppDetectionSettings config,
        bool hasLauncherAncestor = false,
        bool isForeground = false)
    {
        if (IsNonGameShell(normalizedPath, processName))
            return null;

        // Helpers under game trees (or under a launcher parent) must never pin performance.
        if (LooksLikeIdleGameHelper(normalizedPath, processName, workingSetBytes, config.MinWorkingSetMb))
            return null;

        if (config.UseWindowsGpuPreferences && gpuHighPerformancePaths.Contains(normalizedPath))
            return "windowsGpuPreference";

        if (config.UseGameInstallHeuristics && LooksLikeGameInstallPath(normalizedPath))
            return "gameInstallPath";

        if (config.UseGameInstallHeuristics && LooksLikeGameBinaryLayout(normalizedPath))
            return "gameBinaryLayout";

        // Parent chain from NtQuerySystemInformation: sticky signal for titles installed
        // outside known storefront folders (custom Battle.net / standalone library paths).
        if (config.UseGameInstallHeuristics &&
            hasLauncherAncestor &&
            LooksLikeLauncherSpawnedGame(normalizedPath, processName, workingSetBytes, config.MinWorkingSetMb))
            return "launcherChild";

        // Foreground window (GetForegroundWindow): sticky start signal for custom-folder
        // titles that never show a storefront parent and have not yet peaked RAM.
        if (config.UseGameInstallHeuristics &&
            isForeground &&
            LooksLikeForegroundGameCandidate(normalizedPath, processName, workingSetBytes, config.MinWorkingSetMb))
            return "foregroundActive";

        if (config.UseResourceHeuristics && LooksLikeHeavyUserProcess(
                normalizedPath, processName, workingSetBytes, config.MinWorkingSetMb))
            return "resourceHeuristic";

        return null;
    }

    /// <summary>
    /// Foreground alone is not enough (browsers/IDEs/chat). Require a shipping-style binary
    /// name or a moderate working set, and never resource-deny paths/names.
    /// </summary>
    private static bool LooksLikeForegroundGameCandidate(
        string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        if (ResourceDenyPathMarkers.Any(m => normalizedPath.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (ResourceDenyProcessNames.Any(n => string.Equals(processName, n, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (LooksLikeShippingGameBinary(normalizedPath, processName))
            return true;

        long thresholdMb = Math.Max(256, minWorkingSetMb / 2);
        return workingSetBytes / 1024 / 1024 >= thresholdMb;
    }

    private static bool IsNonGameShell(string normalizedPath, string processName)
    {
        if (NonGamePathMarkers.Any(m => normalizedPath.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        // File name alone (path may be empty or under a shared parent like \Riot Games\).
        string file = Path.GetFileNameWithoutExtension(normalizedPath);
        if (IsKnownStorefrontProcess(file) || IsKnownStorefrontProcess(processName))
            return true;

        string name = (processName ?? "").ToLowerInvariant();
        return name is "gamingservices" or "gamingservicesnet" or "gamebar" or "gamebarpresencewriter"
            or "xboxpcapp" or "xboxapp" or "xboxgamebar" or "widgetservice";
    }

    /// <summary>
    /// True when <paramref name="ancestor"/> is a storefront client that commonly parents
    /// game processes. Name match first; path-qualified fallback for ambiguous helpers
    /// (Battle.net <c>Agent.exe</c>, generic <c>Launcher.exe</c> under a known client tree).
    /// </summary>
    private static bool IsLauncherAncestor(ProcessSample ancestor)
    {
        if (IsKnownStorefrontProcess(ancestor.Name))
            return true;

        // Ambiguous short names: only count when the image lives under a known non-game
        // storefront path (e.g. \battle.net\Agent.exe), not arbitrary Agent.exe tools.
        string name = ancestor.Name ?? "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (!name.Equals("Agent", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Launcher", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Bootstrapper", StringComparison.OrdinalIgnoreCase))
            return false;

        string path = ProcessSnapshotProvider.GetPath(ancestor);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = NormalizePath(path);
        return NonGamePathMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Storefront / companion processes that must never keep the performance plan active.
    /// Matched on process or file name (no extension), case-insensitive.
    /// </summary>
    private static bool IsKnownStorefrontProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        // Battle.net uses a dotted product name (Battle.net.exe).
        if (name.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.ToLowerInvariant() switch
        {
            // EA / Origin (FIFA, EA FC, Apex, …)
            "eadesktop" or "eabackgroundservice" or "ealocalhostsvc" or "eacefsubprocess"
                or "eadesktopapplication" or "link2ea" or "origin" or "originwebhelperservice"
                or "originthinsetupinternal" => true,
            // Steam
            "steam" or "steamservice" or "steamwebhelper" or "steamerrorreporter"
                or "gameoverlayui" => true,
            // Epic
            "epicgameslauncher" or "epicwebhelper" or "epicgamesupdater" => true,
            // Riot client shells only — do not list the in-match binary names here
            // (e.g. "League of Legends.exe" / VALORANT-Win64-Shipping stay games).
            "riotclientservices" or "riotclientux" or "riotclientcrashhandler"
                or "riotclient" or "leagueclient" or "leagueclientux" => true,
            // Ubisoft
            "upc" or "ubisoftconnect" or "ubisoftgamelauncher" or "ubisoftextension" => true,
            // GOG
            "galaxyclient" or "galaxyclienthelper" or "galaxyclientservice" => true,
            // Rockstar companions (generic "launcher" is handled via path + helper tokens)
            "socialclubhelper" or "rockstarservice" or "rockstarsteamhelper" => true,
            // Amazon / itch
            "amazongamesui" or "amazongames" => true,
            // Xbox / MS storefront helpers (name-only; path markers cover WindowsApps shells)
            "gamingapp" or "gamingservices" or "gamingservicesnet" or "xboxpcapp"
                or "xboxapp" or "xboxgamebar" or "gamebar" or "gamebarpresencewriter" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True when a non-storefront process under a known launcher parent looks like a real game:
    /// shipping/engine binary naming, or enough working set to rule out tiny helpers.
    /// </summary>
    private static bool LooksLikeLauncherSpawnedGame(
        string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        if (LooksLikeShippingGameBinary(normalizedPath, processName))
            return true;

        // Half the configured resource floor: enough to ignore redistributables / overlays,
        // low enough to catch titles that have not fully loaded assets yet.
        long thresholdMb = Math.Max(256, minWorkingSetMb / 2);
        return workingSetBytes / 1024 / 1024 >= thresholdMb;
    }

    private static bool LooksLikeShippingGameBinary(string normalizedPath, string processName)
    {
        string file = Path.GetFileNameWithoutExtension(normalizedPath);
        string combined = (file + " " + processName);
        return combined.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("-Win64-", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("-Win32-", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("_Win64", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("_Win32", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeGameInstallPath(string normalizedPath)
        => GamePathMarkers.Any(marker => normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeGameBinaryLayout(string normalizedPath)
    {
        if (normalizedPath.Contains(@"\windows\", StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.Contains(@"\windowsapps\", StringComparison.OrdinalIgnoreCase))
            return false;

        return GameBinaryLayouts.Any(marker => normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeIdleGameHelper(string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        // Shipping/game binaries must never be treated as helpers even if the name
        // contains a substring like "client" (FortniteClient-Win64-Shipping).
        if (LooksLikeShippingGameBinary(normalizedPath, processName))
            return false;

        string file = Path.GetFileNameWithoutExtension(normalizedPath);
        string combined = (file + " " + processName).ToLowerInvariant();

        if (IsKnownStorefrontProcess(file) || IsKnownStorefrontProcess(processName))
            return true;

        // Token match: split on non-alnum so "client" does not hit "FortniteClient".
        var tokens = System.Text.RegularExpressions.Regex
            .Split(combined, @"[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pure companion processes: always exclude regardless of working set so a fat
        // Chromium-based launcher (EA Desktop, Epic, …) cannot pin the performance plan.
        string[] alwaysHelperTokens =
        {
            "launcher", "updater", "bootstrapper", "crashhandler", "crashreporter",
            "webhelper", "setup", "uninstall", "redist", "overlay",
            "easyanticheat", "battleye", "beonline", "bootstrap",
            "steamwebhelper", "epicwebhelper", "originwebhelperservice", "galaxyclient",
            "upc", "ubisoftconnect", "eadesktop", "eabackgroundservice", "eacefsubprocess",
            "leagueclient", "leagueclientux", "riotclient", "riotclientservices",
            "riotclientux",
        };
        if (alwaysHelperTokens.Any(h => tokens.Any(t =>
                t.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                (t.Length > h.Length && t.EndsWith(h, StringComparison.OrdinalIgnoreCase)))))
            return true;

        string[] alwaysHelperSubstrings =
        {
            "vc_redist", "eac_launcher", "gog galaxy", "crashpad", "errorreporter",
            "ea desktop", "battle.net", "leagueclient",
        };
        if (alwaysHelperSubstrings.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Ambiguous tokens (service/helper/cef/update) only when the process is small —
        // real game workers can share these words and stay heavy when they use lots of RAM.
        if (workingSetBytes / 1024 / 1024 >= minWorkingSetMb)
            return false;

        string[] softHelperTokens =
        {
            "update", "reporter", "helper", "service", "cef", "qtwebengine", "client",
        };
        if (softHelperTokens.Any(h => tokens.Any(t =>
                t.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                (t.Length > h.Length && t.EndsWith(h, StringComparison.OrdinalIgnoreCase)))))
            return true;

        return false;
    }

    private static bool LooksLikeHeavyUserProcess(string normalizedPath, string processName, long workingSetBytes, int minWorkingSetMb)
    {
        if (workingSetBytes / 1024 / 1024 < minWorkingSetMb) return false;
        if (ResourceDenyPathMarkers.Any(m => normalizedPath.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (ResourceDenyProcessNames.Any(n => string.Equals(processName, n, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
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
                // GpuPreference=2 high-performance GPU; =1 user-default power-saving — ignore.
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

    public static string NormalizePath(string path) => ProcessPathResolver.Normalize(path);

    /// <summary>Runnable checks for classification invariants. Throws on failure.</summary>
    public static void RunSelfCheck()
    {
        var cfg = new HeavyAppDetectionSettings();
        var gpu = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(@"C:\Games\Cyberpunk\bin\x64\Cyberpunk2077.exe"),
        };

        static void Expect(string? actual, string? expected, string caseName)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"SelfCheck failed [{caseName}]: got '{actual}', expected '{expected}'");
        }

        Expect(
            ClassifyProcess(@"C:\Program Files (x86)\Steam\steamapps\common\Game\game.exe", "game", 200 * 1024 * 1024, gpu, cfg),
            "gameInstallPath",
            "steam common");

        Expect(
            ClassifyProcess(@"D:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", "FortniteClient-Win64-Shipping", 400 * 1024 * 1024, gpu, cfg),
            "gameInstallPath",
            "epic + binaries");

        Expect(
            ClassifyProcess(@"C:\Games\MyTitle\Binaries\Win64\MyTitle-Win64-Shipping.exe", "MyTitle-Win64-Shipping", 300 * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cfg),
            "gameBinaryLayout",
            "unreal layout outside storefront");

        Expect(
            ClassifyProcess(@"C:\Games\Cyberpunk\bin\x64\Cyberpunk2077.exe", "Cyberpunk2077", 100 * 1024 * 1024, gpu, cfg),
            "windowsGpuPreference",
            "gpu pref wins");

        Expect(
            ClassifyProcess(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome", 4096L * 1024 * 1024, gpu, cfg),
            null,
            "chrome excluded");

        Expect(
            ClassifyProcess(@"C:\Program Files (x86)\Steam\steamapps\common\Game\GameLauncher.exe", "GameLauncher", 80 * 1024 * 1024, gpu, cfg),
            null,
            "idle launcher skipped");

        // EA Desktop / Origin must never pin performance after FIFA / EA FC (or while the client idles).
        Expect(
            ClassifyProcess(
                @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
                "EADesktop",
                400 * 1024 * 1024,
                gpu,
                cfg),
            null,
            "ea desktop excluded");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EABackgroundService.exe",
                "EABackgroundService",
                120 * 1024 * 1024,
                gpu,
                cfg),
            null,
            "ea background service excluded");

        Expect(
            ClassifyProcess(@"C:\Program Files (x86)\Origin\Origin.exe", "Origin", 300 * 1024 * 1024, gpu, cfg),
            null,
            "origin client excluded");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\EA Games\EA SPORTS FC 25\FC25.exe",
                "FC25",
                500 * 1024 * 1024,
                gpu,
                cfg),
            "gameInstallPath",
            "ea fc game still detected");

        Expect(
            ClassifyProcess(
                @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                "EpicGamesLauncher",
                250 * 1024 * 1024,
                gpu,
                cfg),
            null,
            "epic launcher excluded");

        Expect(
            ClassifyProcess(@"C:\Program Files (x86)\Steam\steam.exe", "steam", 200 * 1024 * 1024, gpu, cfg),
            null,
            "steam client excluded");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\Rockstar Games\Launcher\Launcher.exe",
                "Launcher",
                150 * 1024 * 1024,
                gpu,
                cfg),
            null,
            "rockstar launcher excluded");

        // Fat launcher (above min working set) must still be ignored — only the game should count.
        Expect(
            ClassifyProcess(
                @"C:\Program Files (x86)\Steam\steamapps\common\Game\GameLauncher.exe",
                "GameLauncher",
                2048L * 1024 * 1024,
                gpu,
                cfg),
            null,
            "fat launcher skipped");

        Expect(
            ClassifyProcess(@"C:\Tools\bigtool.exe", "bigtool", 2048L * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cfg),
            "resourceHeuristic",
            "large user process");

        Expect(
            ClassifyProcess(@"C:\Program Files\WindowsApps\Microsoft.XboxApp_1.0.0.0_x64__8wekyb3d8bbwe\XboxApp.exe", "XboxApp", 200 * 1024 * 1024, gpu, cfg),
            null,
            "xbox shell excluded");

        // Custom-folder Unreal title launched by Steam: ancestry is sticky, not resource-only.
        Expect(
            ClassifyProcess(
                @"D:\CustomLibrary\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe",
                "MyGame-Win64-Shipping",
                400 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: true),
            "gameBinaryLayout",
            "custom folder shipping with launcher parent");

        // Blizzard channel folder: sticky layout without launcher parent or huge WS.
        Expect(
            ClassifyProcess(
                @"D:\Games\World of Warcraft\_retail_\Wow.exe",
                "Wow",
                200 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: false),
            "gameBinaryLayout",
            "blizzard retail channel layout");

        Expect(
            ClassifyProcess(
                @"D:\Games\Overwatch\_retail_\Overwatch.exe",
                "Overwatch",
                300 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: false),
            "gameBinaryLayout",
            "overwatch retail channel layout");

        Expect(
            ClassifyProcess(
                @"D:\CustomLibrary\Standalone\StandaloneGame.exe",
                "StandaloneGame",
                900 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: true),
            "launcherChild",
            "custom folder game via launcher ancestry");

        // Foreground custom-folder title: sticky start without launcher parent or huge WS.
        Expect(
            ClassifyProcess(
                @"D:\CustomLibrary\Standalone\StandaloneGame.exe",
                "StandaloneGame",
                900 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: false,
                isForeground: true),
            "foregroundActive",
            "custom folder game via foreground");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                "chrome",
                900 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cfg,
                hasLauncherAncestor: false,
                isForeground: true),
            null,
            "foreground chrome still excluded");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\Riot Games\League of Legends\LeagueClient.exe",
                "LeagueClient",
                600 * 1024 * 1024,
                gpu,
                cfg,
                hasLauncherAncestor: true),
            null,
            "league client excluded under riot path");

        Expect(
            ClassifyProcess(
                @"C:\Program Files\Riot Games\League of Legends\Game\League of Legends.exe",
                "League of Legends",
                1200 * 1024 * 1024,
                gpu,
                cfg,
                hasLauncherAncestor: true),
            "gameInstallPath",
            "lol match binary still detected");

        // Sticky must release a misclassified storefront process once it no longer qualifies.
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [42] = new DetectedHeavyApp
            {
                ProcessId = 42,
                Name = "EADesktop",
                Path = @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
                Reason = "gameInstallPath",
                WorkingSetMb = 400,
            },
        };
        var merged = MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[]
            {
                new ObservedHeavyProcess(
                    42,
                    @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
                    DateTime.UtcNow,
                    "EADesktop",
                    400),
            },
            DateTime.UtcNow);
        if (merged.Count != 0 || sticky.Count != 0)
            throw new InvalidOperationException("SelfCheck failed [sticky drops ea desktop]: launcher remained sticky");

        // Bootstrap PID exit hands sticky to shipping peer under the same Steam title root.
        var handoffSticky = new Dictionary<int, DetectedHeavyApp>
        {
            [91] = new DetectedHeavyApp
            {
                ProcessId = 91,
                Name = "Title",
                Path = @"D:\SteamLibrary\steamapps\common\Title\Title.exe",
                Reason = "gameInstallPath",
                WorkingSetMb = 300,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            },
        };
        var handoffMerged = MergeStickyDetections(
            handoffSticky,
            Array.Empty<DetectedHeavyApp>(),
            new[]
            {
                new ObservedHeavyProcess(
                    92,
                    @"D:\SteamLibrary\steamapps\common\Title\Binaries\Win64\Title-Win64-Shipping.exe",
                    DateTime.UtcNow,
                    "Title-Win64-Shipping",
                    500),
            },
            DateTime.UtcNow);
        if (handoffMerged.Count != 1 || handoffMerged[0].ProcessId != 92 || !handoffSticky.ContainsKey(92))
            throw new InvalidOperationException("SelfCheck failed [sticky install-root handoff]");
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
