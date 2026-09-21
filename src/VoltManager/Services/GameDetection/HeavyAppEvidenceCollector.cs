namespace VoltManager.Services.GameDetection;

internal sealed record HeavyAppProcessEvidence(
    int ProcessId,
    string Name,
    string Path,
    long WorkingSetBytes,
    DateTime? StartedAtUtc,
    bool HasLauncherAncestor,
    bool IsForeground,
    double Gpu3DPercent,
    bool D3dFullscreen);

internal sealed record HeavyAppEvidenceSnapshot(
    DateTime CapturedAtUtc,
    IReadOnlyList<HeavyAppProcessEvidence> Processes,
    IReadOnlySet<string> GpuHighPerformancePaths);

internal sealed class HeavyAppEvidenceCollector
{
    internal static readonly TimeSpan GpuPreferencesCacheDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(4);

    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, ProcessSnapshot> _snapshotProvider;
    private readonly Func<ProcessSample, string> _pathResolver;
    private readonly Func<IReadOnlySet<int>> _presentationProcessIds;
    private readonly Func<int?> _foregroundProcessId;
    private readonly Func<bool> _d3dFullscreenActive;
    private readonly Func<IReadOnlyDictionary<int, double>>? _gpu3DByProcess;
    private readonly Func<HashSet<string>>? _gpuPreferenceReader;
    private readonly Func<ProcessSample, bool> _isLauncherAncestor;
    private readonly object _cacheGate = new();
    private HashSet<string> _cachedGpuPreferences = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _gpuPreferencesRefreshAfterUtc = DateTime.MinValue;

    internal HeavyAppEvidenceCollector(
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, ProcessSnapshot>? snapshotProvider = null,
        Func<ProcessSample, string>? pathResolver = null,
        Func<IReadOnlySet<int>>? presentationProcessIds = null,
        Func<int?>? foregroundProcessId = null,
        Func<bool>? d3dFullscreenActive = null,
        Func<IReadOnlyDictionary<int, double>>? gpu3DByProcess = null,
        Func<HashSet<string>>? gpuPreferenceReader = null,
        Func<ProcessSample, bool>? launcherAncestor = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _snapshotProvider = snapshotProvider ?? ProcessSnapshotProvider.Get;
        _pathResolver = pathResolver ?? (sample => ProcessSnapshotProvider.GetPath(sample));
        _presentationProcessIds = presentationProcessIds ?? ForegroundProcessProbe.TryGetPresentationProcessIds;
        _foregroundProcessId = foregroundProcessId ?? ForegroundProcessProbe.TryGetForegroundProcessId;
        _d3dFullscreenActive = d3dFullscreenActive ?? ForegroundProcessProbe.IsD3dFullscreenActive;
        _gpu3DByProcess = gpu3DByProcess;
        _gpuPreferenceReader = gpuPreferenceReader;
        _isLauncherAncestor = launcherAncestor ?? (_ => false);
    }

    internal HeavyAppEvidenceSnapshot Capture(Models.HeavyAppDetectionSettings config)
    {
        DateTime nowUtc = _utcNow();
        IReadOnlySet<string> gpuPreferences = config.UseWindowsGpuPreferences
            ? GetGpuPreferences(nowUtc)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProcessSnapshot snapshot = _snapshotProvider(SnapshotMaxAge);
        var graph = new ProcessGraph(snapshot.Processes);
        IReadOnlySet<int> presentationPids = _presentationProcessIds();
        int? foregroundPid = _foregroundProcessId();
        bool d3dFullscreenActive = _d3dFullscreenActive();
        IReadOnlyDictionary<int, double> gpu3D = ReadGpu3DSafe();
        var processes = new List<HeavyAppProcessEvidence>(snapshot.Processes.Length);

        foreach (ProcessSample process in snapshot.Processes)
        {
            if (process.Pid == Environment.ProcessId)
                continue;
            try
            {
                string path = _pathResolver(process);
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                bool hasLauncherAncestor = graph.TryFindAncestor(
                    process.Pid, _isLauncherAncestor, maxDepth: 3, out _);
                bool isForeground = presentationPids.Contains(process.Pid);
                gpu3D.TryGetValue(process.Pid, out double gpu3DPercent);
                bool d3dFullscreen = ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
                    d3dFullscreenActive, process.Pid, foregroundPid, presentationPids);

                processes.Add(new HeavyAppProcessEvidence(
                    process.Pid,
                    process.Name,
                    path,
                    process.WorkingSetBytes,
                    process.StartTimeUtc,
                    hasLauncherAncestor,
                    isForeground,
                    gpu3DPercent,
                    d3dFullscreen));
            }
            catch
            {
                // Protected/elevated processes are intentionally skipped.
            }
        }

        return new HeavyAppEvidenceSnapshot(nowUtc, processes, gpuPreferences);
    }

    private IReadOnlyDictionary<int, double> ReadGpu3DSafe()
    {
        if (_gpu3DByProcess == null)
            return new Dictionary<int, double>();
        try
        {
            return _gpu3DByProcess() ?? new Dictionary<int, double>();
        }
        catch
        {
            return new Dictionary<int, double>();
        }
    }

    private IReadOnlySet<string> GetGpuPreferences(DateTime nowUtc)
    {
        lock (_cacheGate)
        {
            if (nowUtc < _gpuPreferencesRefreshAfterUtc)
                return _cachedGpuPreferences;
        }

        HashSet<string> fresh;
        try
        {
            fresh = _gpuPreferenceReader?.Invoke()
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_cacheGate)
        {
            if (nowUtc >= _gpuPreferencesRefreshAfterUtc)
            {
                _cachedGpuPreferences = fresh;
                _gpuPreferencesRefreshAfterUtc = nowUtc + GpuPreferencesCacheDuration;
            }
            return _cachedGpuPreferences;
        }
    }
}
