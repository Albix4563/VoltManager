using System.Diagnostics;
using System.Runtime.InteropServices;
using VoltManager.Services;

namespace VoltManager.Reliability;

internal interface IResourceSelfMonitorScheduler
{
    IDisposable SchedulePeriodic(TimeSpan dueTime, TimeSpan period, Action callback);
}

internal sealed class ThreadPoolResourceSelfMonitorScheduler : IResourceSelfMonitorScheduler
{
    public IDisposable SchedulePeriodic(TimeSpan dueTime, TimeSpan period, Action callback)
        => new System.Threading.Timer(_ => callback(), null, dueTime, period);
}

public sealed record ResourceSelfSample(
    DateTimeOffset TimestampUtc,
    long? HostPrivateBytes,
    long? HostWorkingSetBytes,
    int? HostHandleCount,
    int? HostThreadCount,
    int? HostGdiObjects,
    int? HostUserObjects,
    int WebViewProcessCount,
    long? WebViewPrivateBytes);

internal sealed record ResourceSelfMonitorOptions(
    TimeSpan SampleInterval,
    TimeSpan SummaryInterval,
    TimeSpan WarningRepeatInterval,
    int WindowSize,
    int MinimumGrowthSamples,
    long HostPrivateBytesThreshold,
    int HandleCountThreshold,
    int ThreadCountThreshold,
    int GdiObjectThreshold,
    int UserObjectThreshold,
    double HandleGrowthPerHour,
    double PrivateBytesGrowthPerHour,
    double GdiGrowthPerHour)
{
    public static ResourceSelfMonitorOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(6),
        48,
        12,
        1536L * 1024 * 1024,
        20_000,
        500,
        8_000,
        8_000,
        50,
        50d * 1024 * 1024,
        100);
}

public sealed class ResourceSelfMonitor : IDisposable
{
    private sealed class ConditionState
    {
        public DateTimeOffset LastWarningUtc { get; set; } = DateTimeOffset.MinValue;
        public bool ConditionActive { get; set; }
    }

    private readonly object _sync = new();
    private readonly Func<ResourceSelfSample> _sampler;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ResourceSelfMonitorOptions _options;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Action<string> _error;
    private readonly SingleFlightGate _singleFlight;
    private readonly Queue<ResourceSelfSample> _window = new();
    private readonly Dictionary<string, ConditionState> _conditions = new(StringComparer.Ordinal);
    private IDisposable? _timer;
    private DateTimeOffset _lastSummaryUtc = DateTimeOffset.MinValue;
    private bool _samplingFaulted;
    private bool _stopped;

    public static ResourceSelfSample? LatestForDiagnostics { get; private set; }

    internal int WindowCount
    {
        get { lock (_sync) return _window.Count; }
    }

    internal IReadOnlyList<ResourceSelfSample> WindowSnapshot
    {
        get { lock (_sync) return _window.ToArray(); }
    }

    internal ResourceSelfMonitor(
        Func<ResourceSelfSample> sampler,
        IResourceSelfMonitorScheduler? scheduler = null,
        Func<DateTimeOffset>? utcNow = null,
        ResourceSelfMonitorOptions? options = null,
        Action<string>? info = null,
        Action<string>? warn = null,
        Action<string>? error = null)
    {
        _sampler = sampler;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _options = options ?? ResourceSelfMonitorOptions.Default;
        _info = info ?? Logger.Info;
        _warn = warn ?? Logger.Warn;
        _error = error ?? (message => Logger.Error(message));
        _singleFlight = new SingleFlightGate("Resource self-monitor sample");
        IResourceSelfMonitorScheduler effectiveScheduler = scheduler ?? new ThreadPoolResourceSelfMonitorScheduler();
        _timer = effectiveScheduler.SchedulePeriodic(
            _options.SampleInterval,
            _options.SampleInterval,
            SampleOnce);
    }

    public static ResourceSelfSample CaptureProcessResources(
        Func<IReadOnlyList<int>> webViewPidProvider,
        Func<DateTimeOffset>? utcNow = null)
    {
        DateTimeOffset timestamp = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        long? privateBytes = null;
        long? workingSet = null;
        int? handles = null;
        int? threads = null;
        int? gdi = null;
        int? user = null;

        try
        {
            using Process process = Process.GetCurrentProcess();
            try { process.Refresh(); } catch { /* best-effort: sampled process may exit between polls. */ }
            privateBytes = TryRead(() => process.PrivateMemorySize64);
            workingSet = TryRead(() => process.WorkingSet64);
            handles = TryRead(() => process.HandleCount);
            threads = TryRead(() => process.Threads.Count);
            gdi = TryRead(() => checked((int)GetGuiResources(process.Handle, 0)));
            user = TryRead(() => checked((int)GetGuiResources(process.Handle, 1)));
        }
        catch
        {
            // Each field is best-effort; a process refresh failure must not stop later samples.
        }

        IReadOnlyList<int> pids;
        try { pids = webViewPidProvider() ?? Array.Empty<int>(); }
        catch { pids = Array.Empty<int>(); }

        int[] webViewPids = pids.Distinct().ToArray();
        long webViewPrivateBytes = 0;
        int webViewCount = webViewPids.Length;
        bool anyWebViewPrivateBytes = false;
        foreach (int pid in webViewPids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                process.Refresh();
                webViewPrivateBytes = checked(webViewPrivateBytes + process.PrivateMemorySize64);
                anyWebViewPrivateBytes = true;
            }
            catch
            {
                // Processes can exit between GetProcessInfos and sampling.
            }
        }

        return new ResourceSelfSample(
            timestamp,
            privateBytes,
            workingSet,
            handles,
            threads,
            gdi,
            user,
            webViewCount,
            anyWebViewPrivateBytes ? webViewPrivateBytes : null);
    }

    public void Stop()
    {
        IDisposable? timer;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            timer = _timer;
            _timer = null;
        }

        try { timer?.Dispose(); }
        catch (Exception ex) { Logger.Warn("Resource self-monitor timer disposal failed: " + ex.Message); }
    }

    public void Dispose() => Stop();

    internal void SampleOnce()
    {
        if (!_singleFlight.TryEnter()) return;
        try
        {
            lock (_sync)
            {
                if (_stopped) return;
            }

            ResourceSelfSample sample;
            try
            {
                sample = _sampler() with { TimestampUtc = _utcNow() };
                _samplingFaulted = false;
            }
            catch (Exception ex)
            {
                _samplingFaulted = Logger.WarnOnce(_samplingFaulted, "Resource self-monitor sampling failed", ex);
                return;
            }

            lock (_sync)
            {
                if (_stopped) return;
                LatestForDiagnostics = sample;
                _window.Enqueue(sample);
                while (_window.Count > _options.WindowSize)
                    _window.Dequeue();

                Evaluate(sample);
                if (_lastSummaryUtc == DateTimeOffset.MinValue ||
                    sample.TimestampUtc - _lastSummaryUtc >= _options.SummaryInterval)
                {
                    _lastSummaryUtc = sample.TimestampUtc;
                    _info(FormatSummary(sample));
                }
            }
        }
        finally
        {
            _singleFlight.Exit();
        }
    }

    internal static double? LeastSquaresSlopePerHour(
        IReadOnlyList<ResourceSelfSample> samples,
        Func<ResourceSelfSample, double?> selector)
    {
        var points = samples
            .Select(sample => (sample.TimestampUtc, Value: selector(sample)))
            .Where(point => point.Value.HasValue)
            .Select(point => (point.TimestampUtc, Value: point.Value!.Value))
            .ToArray();
        if (points.Length < 2) return null;

        DateTimeOffset origin = points[0].TimestampUtc;
        double meanX = points.Average(point => (point.TimestampUtc - origin).TotalHours);
        double meanY = points.Average(point => point.Value);
        double numerator = 0;
        double denominator = 0;
        foreach (var point in points)
        {
            double x = (point.TimestampUtc - origin).TotalHours;
            double dx = x - meanX;
            numerator += dx * (point.Value - meanY);
            denominator += dx * dx;
        }
        return denominator <= double.Epsilon ? null : numerator / denominator;
    }

    internal static bool IsSustainedGrowth(
        IReadOnlyList<ResourceSelfSample> samples,
        Func<ResourceSelfSample, double?> selector,
        int minimumSamples,
        double minimumSlopePerHour)
    {
        var values = samples
            .Select(sample => (sample.TimestampUtc, Value: selector(sample)))
            .Where(item => item.Value.HasValue)
            .Select(item => (item.TimestampUtc, Value: item.Value!.Value))
            .ToArray();
        if (values.Length < minimumSamples) return false;

        double? slope = LeastSquaresSlopePerHour(
            samples,
            sample => selector(sample));
        if (slope is null || slope.Value < minimumSlopePerHour) return false;

        int nonDecreasing = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i].Value >= values[i - 1].Value)
                nonDecreasing++;
        }

        return nonDecreasing >= Math.Ceiling((values.Length - 1) * 0.60)
            && values[^1].Value > values[0].Value;
    }

    private void Evaluate(ResourceSelfSample sample)
    {
        EvaluateCondition("host-private-bytes", sample.HostPrivateBytes > _options.HostPrivateBytesThreshold,
            false, $"Host private bytes exceeded {_options.HostPrivateBytesThreshold}: {sample.HostPrivateBytes}", sample.TimestampUtc);
        EvaluateCondition("handles", sample.HostHandleCount > _options.HandleCountThreshold,
            false, $"Host handle count exceeded {_options.HandleCountThreshold}: {sample.HostHandleCount}", sample.TimestampUtc);
        EvaluateCondition("threads", sample.HostThreadCount > _options.ThreadCountThreshold,
            false, $"Host thread count exceeded {_options.ThreadCountThreshold}: {sample.HostThreadCount}", sample.TimestampUtc);
        EvaluateCondition("gdi", sample.HostGdiObjects > _options.GdiObjectThreshold,
            true, $"Host GDI object count exceeded {_options.GdiObjectThreshold}: {sample.HostGdiObjects}", sample.TimestampUtc);
        EvaluateCondition("user", sample.HostUserObjects > _options.UserObjectThreshold,
            true, $"Host USER object count exceeded {_options.UserObjectThreshold}: {sample.HostUserObjects}", sample.TimestampUtc);

        ResourceSelfSample[] window = _window
            .TakeLast(_options.MinimumGrowthSamples)
            .ToArray();
        EvaluateCondition("growth-handles",
            IsSustainedGrowth(window, s => s.HostHandleCount, _options.MinimumGrowthSamples, _options.HandleGrowthPerHour),
            false, $"Sustained host handle growth detected (>{_options.HandleGrowthPerHour:F0}/h).", sample.TimestampUtc);
        EvaluateCondition("growth-private-bytes",
            IsSustainedGrowth(window, s => s.HostPrivateBytes, _options.MinimumGrowthSamples, _options.PrivateBytesGrowthPerHour),
            false, $"Sustained host private-byte growth detected (>{_options.PrivateBytesGrowthPerHour / (1024 * 1024):F0} MiB/h).", sample.TimestampUtc);
        EvaluateCondition("growth-gdi",
            IsSustainedGrowth(window, s => s.HostGdiObjects, _options.MinimumGrowthSamples, _options.GdiGrowthPerHour),
            false, $"Sustained host GDI-object growth detected (>{_options.GdiGrowthPerHour:F0}/h).", sample.TimestampUtc);
    }

    private void EvaluateCondition(string key, bool condition, bool error, string message, DateTimeOffset now)
    {
        if (!_conditions.TryGetValue(key, out ConditionState? state))
        {
            state = new ConditionState();
            _conditions[key] = state;
        }

        if (!condition)
        {
            if (state.ConditionActive)
            {
                state.ConditionActive = false;
                _info("Resource self-monitor condition cleared: " + key);
            }
            return;
        }

        state.ConditionActive = true;

        if (now - state.LastWarningUtc < _options.WarningRepeatInterval)
            return;

        state.LastWarningUtc = now;
        if (error) _error(message);
        else _warn(message);
    }

    private static string FormatSummary(ResourceSelfSample sample)
        => $"Resource sample: private={FormatMiB(sample.HostPrivateBytes)}MiB, ws={FormatMiB(sample.HostWorkingSetBytes)}MiB, " +
           $"handles={sample.HostHandleCount?.ToString() ?? "n/a"}, threads={sample.HostThreadCount?.ToString() ?? "n/a"}, " +
           $"gdi={sample.HostGdiObjects?.ToString() ?? "n/a"}, user={sample.HostUserObjects?.ToString() ?? "n/a"}, " +
           $"webview={sample.WebViewProcessCount}/{FormatMiB(sample.WebViewPrivateBytes)}MiB";

    private static string FormatMiB(long? bytes)
        => bytes.HasValue ? (bytes.Value / (1024d * 1024)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch { return null; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
