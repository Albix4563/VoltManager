using VoltManager.Models;

namespace VoltManager.Services.GameDetection;

internal sealed record HeavyAppClassificationResult(
    IReadOnlyList<DetectedHeavyApp> Detected,
    IReadOnlyList<ObservedHeavyProcess> Observed);

internal static class HeavyAppClassifier
{
    internal static HeavyAppClassificationResult Classify(
        HeavyAppEvidenceSnapshot evidence,
        HeavyAppDetectionSettings config)
    {
        var detected = new List<DetectedHeavyApp>();
        var observed = new List<ObservedHeavyProcess>(evidence.Processes.Count);

        foreach (HeavyAppProcessEvidence process in evidence.Processes)
        {
            long workingSetMb = Math.Max(0, process.WorkingSetBytes / 1024 / 1024);
            observed.Add(new ObservedHeavyProcess(
                process.ProcessId, process.Path, process.StartedAtUtc, process.Name, workingSetMb));

            bool explicitPriority = HeavyAppDetectionService.MatchesExactExecutablePath(
                process.Path, config.PriorityApplicationPaths);
            if (!config.Enabled)
            {
                if (explicitPriority)
                    detected.Add(CreatePriorityDetection(process, workingSetMb));
                continue;
            }

            GameDetectionAssessment assessment = HeavyAppDetectionService.AssessProcess(
                process.Path,
                process.Name,
                process.WorkingSetBytes,
                evidence.GpuHighPerformancePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                config,
                process.StartedAtUtc,
                evidence.CapturedAtUtc,
                process.HasLauncherAncestor,
                process.IsForeground,
                process.Gpu3DPercent,
                process.D3dFullscreen);

            string? kind = HeavyAppDetectionService.ClassifyKind(
                assessment,
                HeavyAppDetectionService.NormalizePath(process.Path),
                process.Name,
                process.WorkingSetBytes,
                config);
            if (kind == null)
                continue;
            if (kind == "priorityApp")
            {
                detected.Add(CreatePriorityDetection(process, workingSetMb));
                continue;
            }

            detected.Add(new DetectedHeavyApp
            {
                ProcessId = process.ProcessId,
                Name = string.IsNullOrWhiteSpace(process.Name)
                    ? Path.GetFileNameWithoutExtension(process.Path)
                    : process.Name,
                Path = process.Path,
                Reason = assessment.PrimaryReason!,
                Kind = kind,
                WorkingSetMb = workingSetMb,
                StartedAtUtc = process.StartedAtUtc,
                ConfidenceScore = assessment.Score,
                ConfidenceLevel = assessment.Level,
                Evidence = assessment.Evidence,
            });
        }

        return new HeavyAppClassificationResult(detected, observed);
    }

    private static DetectedHeavyApp CreatePriorityDetection(
        HeavyAppProcessEvidence process,
        long workingSetMb)
        => new()
        {
            ProcessId = process.ProcessId,
            Name = string.IsNullOrWhiteSpace(process.Name)
                ? Path.GetFileNameWithoutExtension(process.Path)
                : process.Name,
            Path = process.Path,
            Reason = "priorityApplication",
            Kind = "priorityApp",
            WorkingSetMb = workingSetMb,
            StartedAtUtc = process.StartedAtUtc,
            ConfidenceScore = 100,
            ConfidenceLevel = "explicit",
        };
}
