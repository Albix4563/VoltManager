namespace VoltManager.WindowsHarness;

internal enum SoakVerdict
{
    Passed,
    Failed,
    NotVerified,
}

internal sealed record SoakThresholds(
    double HostPrivateBytesPerHour,
    double GroupPrivateBytesPerHour,
    double HandlesPerHour,
    double GuiObjectsPerHour,
    double ThreadsPerHour,
    int ProcessCountGrowth,
    int GuiObjectLimit,
    int MaxConsecutiveHungSamples,
    int MinimumSamples)
{
    public static SoakThresholds Default { get; } = new(
        30d * 1024 * 1024,
        60d * 1024 * 1024,
        200,
        100,
        20,
        2,
        8000,
        2,
        4);
}

internal sealed record SoakSample(
    double ElapsedSeconds,
    long HostPrivateBytes,
    long GroupPrivateBytes,
    long HostWorkingSetBytes,
    long GroupWorkingSetBytes,
    int HostHandleCount,
    int GroupHandleCount,
    int HostThreadCount,
    int GroupThreadCount,
    int? HostGdiObjects,
    int? HostUserObjects,
    int GroupProcessCount,
    bool MainWindowResponsive,
    bool RootAlive,
    bool HostAlive,
    bool ChildCrashDetected);

internal sealed record SoakCheckResult(string Name, SoakVerdict Verdict, string Detail);

internal sealed record SoakAnalysis(
    SoakVerdict Verdict,
    int MeasurementSampleCount,
    IReadOnlyDictionary<string, double> SlopesPerHour,
    IReadOnlyList<SoakCheckResult> Checks);

internal static class SoakAnalyzer
{
    public static SoakAnalysis Analyze(
        IReadOnlyList<SoakSample> samples,
        TimeSpan warmup,
        SoakThresholds thresholds)
    {
        var checks = new List<SoakCheckResult>();
        IReadOnlyList<SoakSample> measured = samples
            .Where(sample => sample.ElapsedSeconds >= warmup.TotalSeconds)
            .ToArray();

        bool processFailure = samples.Any(sample =>
            !sample.RootAlive || !sample.HostAlive || sample.ChildCrashDetected);
        checks.Add(new SoakCheckResult(
            "process_survival",
            processFailure ? SoakVerdict.Failed : SoakVerdict.Passed,
            processFailure
                ? "application, supervisor, or child process exited unexpectedly"
                : "application process group stayed alive"));

        int longestHang = LongestRun(samples, sample => !sample.MainWindowResponsive);
        checks.Add(new SoakCheckResult(
            "window_responsiveness",
            longestHang > thresholds.MaxConsecutiveHungSamples ? SoakVerdict.Failed : SoakVerdict.Passed,
            $"longest hung run {longestHang} samples; allowed {thresholds.MaxConsecutiveHungSamples}"));

        int maxGdi = samples.Where(sample => sample.HostGdiObjects.HasValue)
            .Select(sample => sample.HostGdiObjects!.Value).DefaultIfEmpty().Max();
        int maxUser = samples.Where(sample => sample.HostUserObjects.HasValue)
            .Select(sample => sample.HostUserObjects!.Value).DefaultIfEmpty().Max();
        bool guiLimitFailed = maxGdi > thresholds.GuiObjectLimit || maxUser > thresholds.GuiObjectLimit;
        checks.Add(new SoakCheckResult(
            "gui_object_limit",
            guiLimitFailed ? SoakVerdict.Failed : SoakVerdict.Passed,
            $"max GDI {maxGdi}, USER {maxUser}; limit {thresholds.GuiObjectLimit}"));

        if (measured.Count < thresholds.MinimumSamples)
        {
            checks.Add(new SoakCheckResult(
                "sample_sufficiency",
                SoakVerdict.NotVerified,
                $"{measured.Count} post-warmup samples; minimum {thresholds.MinimumSamples}"));
            return BuildAnalysis(measured, checks, ComputeSlopes(measured));
        }

        checks.Add(new SoakCheckResult(
            "sample_sufficiency",
            SoakVerdict.Passed,
            $"{measured.Count} post-warmup samples"));

        var slopes = ComputeSlopes(measured);
        AddGrowthCheck(
            checks, "host_private_growth", measured, slopes["host_private_bytes"],
            thresholds.HostPrivateBytesPerHour, sample => sample.HostPrivateBytes, "bytes/h");
        AddGrowthCheck(
            checks, "group_private_growth", measured, slopes["group_private_bytes"],
            thresholds.GroupPrivateBytesPerHour, sample => sample.GroupPrivateBytes, "bytes/h");
        AddGrowthCheck(
            checks, "group_handle_growth", measured, slopes["group_handles"],
            thresholds.HandlesPerHour, sample => sample.GroupHandleCount, "handles/h");
        AddNullableGrowthCheck(
            checks, "gdi_growth", measured, slopes["host_gdi_objects"],
            thresholds.GuiObjectsPerHour, sample => sample.HostGdiObjects, "objects/h",
            thresholds.MinimumSamples);
        AddNullableGrowthCheck(
            checks, "user_growth", measured, slopes["host_user_objects"],
            thresholds.GuiObjectsPerHour, sample => sample.HostUserObjects, "objects/h",
            thresholds.MinimumSamples);
        AddGrowthCheck(
            checks, "group_thread_growth", measured, slopes["group_threads"],
            thresholds.ThreadsPerHour, sample => sample.GroupThreadCount, "threads/h");

        double processGrowth = WindowAverage(measured, sample => sample.GroupProcessCount, fromStart: false)
            - WindowAverage(measured, sample => sample.GroupProcessCount, fromStart: true);
        bool processGrowthFailed = processGrowth > thresholds.ProcessCountGrowth;
        checks.Add(new SoakCheckResult(
            "process_count_growth",
            processGrowthFailed ? SoakVerdict.Failed : SoakVerdict.Passed,
            $"growth {processGrowth:0.###}; allowed {thresholds.ProcessCountGrowth}"));

        return BuildAnalysis(measured, checks, slopes);
    }

    private static SoakAnalysis BuildAnalysis(
        IReadOnlyList<SoakSample> measured,
        IReadOnlyList<SoakCheckResult> checks,
        IReadOnlyDictionary<string, double> slopes)
    {
        SoakVerdict verdict = checks.Any(check => check.Verdict == SoakVerdict.Failed)
            ? SoakVerdict.Failed
            : checks.Any(check => check.Verdict == SoakVerdict.NotVerified)
                ? SoakVerdict.NotVerified
                : SoakVerdict.Passed;
        return new SoakAnalysis(verdict, measured.Count, slopes, checks);
    }

    private static Dictionary<string, double> ComputeSlopes(IReadOnlyList<SoakSample> samples)
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["host_private_bytes"] = SlopePerHour(samples, sample => sample.HostPrivateBytes),
            ["group_private_bytes"] = SlopePerHour(samples, sample => sample.GroupPrivateBytes),
            ["host_working_set_bytes"] = SlopePerHour(samples, sample => sample.HostWorkingSetBytes),
            ["group_working_set_bytes"] = SlopePerHour(samples, sample => sample.GroupWorkingSetBytes),
            ["host_handles"] = SlopePerHour(samples, sample => sample.HostHandleCount),
            ["group_handles"] = SlopePerHour(samples, sample => sample.GroupHandleCount),
            ["host_threads"] = SlopePerHour(samples, sample => sample.HostThreadCount),
            ["group_threads"] = SlopePerHour(samples, sample => sample.GroupThreadCount),
            ["host_gdi_objects"] = NullableSlopePerHour(samples, sample => sample.HostGdiObjects),
            ["host_user_objects"] = NullableSlopePerHour(samples, sample => sample.HostUserObjects),
            ["process_count"] = SlopePerHour(samples, sample => sample.GroupProcessCount),
        };
    }

    private static void AddGrowthCheck(
        ICollection<SoakCheckResult> checks,
        string name,
        IReadOnlyList<SoakSample> samples,
        double slope,
        double threshold,
        Func<SoakSample, double> selector,
        string unit)
    {
        bool failed = IsSustainedGrowth(samples, slope, threshold, selector);
        checks.Add(new SoakCheckResult(
            name,
            failed ? SoakVerdict.Failed : SoakVerdict.Passed,
            $"slope {slope:0.###} {unit}; limit {threshold:0.###} {unit}"));
    }

    private static void AddNullableGrowthCheck(
        ICollection<SoakCheckResult> checks,
        string name,
        IReadOnlyList<SoakSample> samples,
        double slope,
        double threshold,
        Func<SoakSample, int?> selector,
        string unit,
        int minimumSamples)
    {
        SoakSample[] available = samples.Where(sample => selector(sample).HasValue).ToArray();
        if (available.Length < minimumSamples)
        {
            checks.Add(new SoakCheckResult(
                name,
                SoakVerdict.NotVerified,
                $"{available.Length} samples available; minimum {minimumSamples}"));
            return;
        }

        bool failed = IsSustainedGrowth(
            available,
            slope,
            threshold,
            sample => selector(sample)!.Value);
        checks.Add(new SoakCheckResult(
            name,
            failed ? SoakVerdict.Failed : SoakVerdict.Passed,
            $"slope {slope:0.###} {unit}; limit {threshold:0.###} {unit}"));
    }

    private static bool IsSustainedGrowth(
        IReadOnlyList<SoakSample> samples,
        double slope,
        double threshold,
        Func<SoakSample, double> selector)
    {
        double tolerance = Math.Max(1e-9, Math.Abs(threshold) * 1e-9);
        if (samples.Count < 2 || slope <= threshold + tolerance)
            return false;

        double observedHours = Math.Max(
            0,
            (samples[^1].ElapsedSeconds - samples[0].ElapsedSeconds) / 3600d);
        if (observedHours <= 0)
            return false;

        double first = WindowAverage(samples, selector, fromStart: true);
        double last = WindowAverage(samples, selector, fromStart: false);
        return last - first > threshold * observedHours * 0.5;
    }

    private static double WindowAverage(
        IReadOnlyList<SoakSample> samples,
        Func<SoakSample, double> selector,
        bool fromStart)
    {
        int count = Math.Clamp(samples.Count / 5, 1, 5);
        IEnumerable<SoakSample> window = fromStart ? samples.Take(count) : samples.Skip(samples.Count - count);
        return window.Average(selector);
    }

    private static double SlopePerHour(
        IReadOnlyList<SoakSample> samples,
        Func<SoakSample, double> selector)
        => LeastSquaresSlopePerHour(samples.Select(sample => (sample.ElapsedSeconds, selector(sample))).ToArray());

    private static double NullableSlopePerHour(
        IReadOnlyList<SoakSample> samples,
        Func<SoakSample, int?> selector)
        => LeastSquaresSlopePerHour(samples
            .Where(sample => selector(sample).HasValue)
            .Select(sample => (sample.ElapsedSeconds, (double)selector(sample)!.Value))
            .ToArray());

    private static double LeastSquaresSlopePerHour(IReadOnlyList<(double Seconds, double Value)> points)
    {
        if (points.Count < 2)
            return 0;

        double origin = points[0].Seconds;
        double meanX = points.Average(point => (point.Seconds - origin) / 3600d);
        double meanY = points.Average(point => point.Value);
        double numerator = 0;
        double denominator = 0;
        foreach (var point in points)
        {
            double x = (point.Seconds - origin) / 3600d - meanX;
            numerator += x * (point.Value - meanY);
            denominator += x * x;
        }
        return denominator <= double.Epsilon ? 0 : numerator / denominator;
    }

    private static int LongestRun(IReadOnlyList<SoakSample> samples, Func<SoakSample, bool> predicate)
    {
        int longest = 0;
        int current = 0;
        foreach (SoakSample sample in samples)
        {
            current = predicate(sample) ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }
}
