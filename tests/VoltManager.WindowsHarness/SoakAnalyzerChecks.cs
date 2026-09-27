namespace VoltManager.WindowsHarness;

internal static class SoakAnalyzerChecks
{
    public static void Add(HarnessReport report)
    {
        Add(report, "soak_flat_noise_passes", FlatNoisePasses,
            "least-squares analyzer tolerates bounded flat noise");
        Add(report, "soak_steady_growth_fails", SteadyGrowthFails,
            "sustained private-byte growth above threshold is rejected");
        Add(report, "soak_warmup_step_passes", WarmupStepPasses,
            "one-time cache growth before the warmup boundary is ignored");
        Add(report, "soak_crash_fails", CrashFails,
            "unexpected child exit is a hard failure");
        Add(report, "soak_two_hangs_allowed", TwoHangsAllowed,
            "two consecutive unresponsive samples stay within the limit");
        Add(report, "soak_three_hangs_fail", ThreeHangsFail,
            "more than two consecutive unresponsive samples fails");
        Add(report, "soak_too_few_samples_not_verified", TooFewSamplesNotVerified,
            "insufficient post-warmup evidence is not reported as a pass");
        Add(report, "soak_growth_boundary_passes", GrowthBoundaryPasses,
            "growth exactly on the configured slope threshold passes");
        Add(report, "soak_growth_above_boundary_fails", GrowthAboveBoundaryFails,
            "growth immediately above the configured slope threshold fails");
        Add(report, "soak_gui_boundary", GuiBoundaryIsStrict,
            "GDI/USER limit fails only above the configured boundary");
        Add(report, "soak_process_growth_boundary", ProcessGrowthBoundaryIsStrict,
            "process-count growth of two passes while growth of three fails");
        Add(report, "soak_working_set_slope_computed", WorkingSetSlopeComputed,
            "working-set slope is computed even though it has no failure threshold");
    }

    private static bool FlatNoisePasses()
    {
        int[] noise = [0, 2, -2, 1, -1, -2, 2, 0];
        SoakSample[] samples = Series(noise.Length, i => Stable(i) with
        {
            HostPrivateBytes = 100_000_000 + noise[i] * 100_000,
            GroupPrivateBytes = 200_000_000 - noise[i] * 150_000,
            GroupHandleCount = 300 + noise[i],
            GroupThreadCount = 40 - noise[i],
            HostGdiObjects = 120 + noise[i],
            HostUserObjects = 80 - noise[i],
        });
        return SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default).Verdict == SoakVerdict.Passed;
    }

    private static bool SteadyGrowthFails()
    {
        SoakSample[] samples = Series(8, i => Stable(i) with
        {
            HostPrivateBytes = 100_000_000 + i * 2_000_000,
        });
        return SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default)
            .Checks.Any(check => check.Name == "host_private_growth" && check.Verdict == SoakVerdict.Failed);
    }

    private static bool WarmupStepPasses()
    {
        SoakSample[] samples = Series(8, i => Stable(i) with
        {
            HostPrivateBytes = i == 0 ? 100_000_000 : 180_000_000,
            GroupPrivateBytes = i == 0 ? 200_000_000 : 280_000_000,
        });
        return SoakAnalyzer.Analyze(samples, TimeSpan.FromMinutes(2), SoakThresholds.Default).Verdict
            == SoakVerdict.Passed;
    }

    private static bool CrashFails()
    {
        SoakSample[] samples = Series(6, i => Stable(i) with { ChildCrashDetected = i == 3 });
        return SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default).Verdict == SoakVerdict.Failed;
    }

    private static bool TwoHangsAllowed()
    {
        SoakSample[] samples = Series(6, i => Stable(i) with
        {
            MainWindowResponsive = i is not (2 or 3),
        });
        SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default);
        return analysis.Checks.Any(check =>
            check.Name == "window_responsiveness" && check.Verdict == SoakVerdict.Passed);
    }

    private static bool ThreeHangsFail()
    {
        SoakSample[] samples = Series(7, i => Stable(i) with
        {
            MainWindowResponsive = i is not (2 or 3 or 4),
        });
        SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default);
        return analysis.Checks.Any(check =>
            check.Name == "window_responsiveness" && check.Verdict == SoakVerdict.Failed);
    }

    private static bool TooFewSamplesNotVerified()
    {
        SoakSample[] samples = Series(3, Stable);
        return SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default).Verdict
            == SoakVerdict.NotVerified;
    }

    private static bool GrowthBoundaryPasses()
    {
        SoakThresholds thresholds = SoakThresholds.Default with { HostPrivateBytesPerHour = 3600 };
        SoakSample[] samples = Series(8, i => Stable(i) with { HostPrivateBytes = 1_000_000 + i * 60 });
        SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, TimeSpan.Zero, thresholds);
        return analysis.Checks.Any(check =>
            check.Name == "host_private_growth" && check.Verdict == SoakVerdict.Passed);
    }

    private static bool GrowthAboveBoundaryFails()
    {
        SoakThresholds thresholds = SoakThresholds.Default with { HostPrivateBytesPerHour = 3599 };
        SoakSample[] samples = Series(8, i => Stable(i) with { HostPrivateBytes = 1_000_000 + i * 60 });
        SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, TimeSpan.Zero, thresholds);
        return analysis.Checks.Any(check =>
            check.Name == "host_private_growth" && check.Verdict == SoakVerdict.Failed);
    }

    private static bool GuiBoundaryIsStrict()
    {
        SoakThresholds thresholds = SoakThresholds.Default with { GuiObjectLimit = 8000 };
        SoakSample[] boundary = Series(5, i => Stable(i) with
        {
            HostGdiObjects = 8000,
            HostUserObjects = 8000,
        });
        SoakSample[] above = Series(5, i => Stable(i) with
        {
            HostGdiObjects = 8001,
            HostUserObjects = 8001,
        });
        return SoakAnalyzer.Analyze(boundary, TimeSpan.Zero, thresholds).Verdict == SoakVerdict.Passed
            && SoakAnalyzer.Analyze(above, TimeSpan.Zero, thresholds).Verdict == SoakVerdict.Failed;
    }

    private static bool ProcessGrowthBoundaryIsStrict()
    {
        SoakSample[] boundary = Series(10, i => Stable(i) with
        {
            GroupProcessCount = i < 5 ? 5 : 7,
        });
        SoakSample[] above = Series(10, i => Stable(i) with
        {
            GroupProcessCount = i < 5 ? 5 : 8,
        });
        SoakAnalysis boundaryAnalysis = SoakAnalyzer.Analyze(boundary, TimeSpan.Zero, SoakThresholds.Default);
        SoakAnalysis aboveAnalysis = SoakAnalyzer.Analyze(above, TimeSpan.Zero, SoakThresholds.Default);
        return boundaryAnalysis.Checks.Any(check =>
                check.Name == "process_count_growth" && check.Verdict == SoakVerdict.Passed)
            && aboveAnalysis.Checks.Any(check =>
                check.Name == "process_count_growth" && check.Verdict == SoakVerdict.Failed);
    }

    private static bool WorkingSetSlopeComputed()
    {
        SoakSample[] samples = Series(6, i => Stable(i) with
        {
            HostWorkingSetBytes = 50_000_000 + i * 1000,
        });
        SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, TimeSpan.Zero, SoakThresholds.Default);
        return analysis.SlopesPerHour["host_working_set_bytes"] > 0;
    }

    private static SoakSample[] Series(int count, Func<int, SoakSample> factory)
        => Enumerable.Range(0, count).Select(factory).ToArray();

    private static SoakSample Stable(int index)
        => new(
            index * 60,
            100_000_000,
            200_000_000,
            80_000_000,
            160_000_000,
            100,
            300,
            20,
            40,
            120,
            80,
            5,
            true,
            true,
            true,
            false);

    private static void Add(HarnessReport report, string name, Func<bool> check, string detail)
    {
        try
        {
            report.Checks.Add(new HarnessCheck(name, check() ? "passed" : "failed", detail));
        }
        catch (Exception ex)
        {
            report.Checks.Add(new HarnessCheck(name, "failed", ex.Message));
        }
    }
}
