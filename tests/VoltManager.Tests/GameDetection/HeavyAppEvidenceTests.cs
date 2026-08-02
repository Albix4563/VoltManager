using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class HeavyAppEvidenceTests
{
    [Fact]
    public void AssessProcess_keeps_primary_reason_and_collects_all_matching_signals()
    {
        var config = new HeavyAppDetectionSettings();
        string path = HeavyAppDetectionService.NormalizePath(
            @"D:\Steam\steamapps\common\Title\Binaries\Win64\Title-Win64-Shipping.exe");
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        var result = HeavyAppDetectionService.AssessProcess(
            path,
            "Title-Win64-Shipping",
            2L * 1024 * 1024 * 1024,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            config,
            now.AddMinutes(-3),
            now,
            hasLauncherAncestor: true);

        Assert.Equal("gameInstallPath", result.PrimaryReason);
        Assert.Contains(result.Evidence, evidence => evidence.Code == "gameInstallPath");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "gameBinaryLayout");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "resourceHeuristic");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "launcherAncestry");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "duration15s");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "duration2m");
        Assert.Equal(67, result.Score);
        Assert.Equal("probable", result.Level);
    }

    [Fact]
    public void AssessProcess_preserves_gpu_preference_as_highest_priority_reason()
    {
        var config = new HeavyAppDetectionSettings();
        string path = HeavyAppDetectionService.NormalizePath(
            @"C:\Games\Known\Binaries\Win64\Known-Win64-Shipping.exe");
        var gpuPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

        var result = HeavyAppDetectionService.AssessProcess(
            path,
            "Known-Win64-Shipping",
            512L * 1024 * 1024,
            gpuPreferences,
            config);

        Assert.Equal("windowsGpuPreference", result.PrimaryReason);
        Assert.Contains(result.Evidence, evidence => evidence.Code == "windowsGpuPreference");
        Assert.Contains(result.Evidence, evidence => evidence.Code == "gameBinaryLayout");
    }

    [Fact]
    public void AssessProcess_rejects_known_storefront_before_adding_evidence()
    {
        var result = HeavyAppDetectionService.AssessProcess(
            @"C:\Program Files (x86)\Steam\steam.exe",
            "steam",
            2L * 1024 * 1024 * 1024,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HeavyAppDetectionSettings(),
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow,
            hasLauncherAncestor: true);

        Assert.Null(result.PrimaryReason);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Meaningful_change_includes_confidence_and_evidence_updates()
    {
        var previous = StateWith(new DetectedHeavyApp
        {
            ProcessId = 42,
            Path = @"C:\Games\Title\game.exe",
            Reason = "gameInstallPath",
            ConfidenceScore = 30,
            ConfidenceLevel = "ignored",
            Evidence = new[]
            {
                new GameDetectionEvidence("gameInstallPath", "provenance", 30, "Known game installation path"),
            },
        });
        var next = StateWith(previous.ActiveProcesses[0] with
        {
            ConfidenceScore = 34,
            ConfidenceLevel = "ignored",
            Evidence = previous.ActiveProcesses[0].Evidence.Append(
                new GameDetectionEvidence("duration15s", "runtime", 4, "Running for at least 15 seconds")).ToArray(),
        });

        Assert.True(HeavyAppDetectionService.HasMeaningfulChange(previous, next));
        Assert.False(HeavyAppDetectionService.HasMeaningfulChange(next, next));
    }

    private static HeavyAppDetectionState StateWith(DetectedHeavyApp process)
        => new()
        {
            Enabled = true,
            Active = true,
            DetectedCount = 1,
            ActiveProcesses = new List<DetectedHeavyApp> { process },
        };
}
