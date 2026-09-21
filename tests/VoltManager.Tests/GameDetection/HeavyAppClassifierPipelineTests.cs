using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public sealed class HeavyAppClassifierPipelineTests
{
    private const long Mb = 1024L * 1024L;

    [Fact]
    public void Classifier_preserves_priority_and_missing_gpu_behavior()
    {
        DateTime now = new(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc);
        string priorityPath = @"D:\Tools\Renderer\Renderer.exe";
        var priority = Snapshot(now, new HeavyAppProcessEvidence(51, "Renderer", priorityPath, 64 * Mb, now.AddMinutes(-3), false, false, 0, false));
        var config = new HeavyAppDetectionSettings { Enabled = false, PriorityApplicationPaths = new List<string> { priorityPath } };
        var priorityResult = Assert.Single(HeavyAppClassifier.Classify(priority, config).Detected);
        Assert.Equal("priorityApp", priorityResult.Kind);
        Assert.Equal("priorityApplication", priorityResult.Reason);

        string gamePath = @"D:\SteamLibrary\steamapps\common\Stardewish\Stardewish.exe";
        var game = Snapshot(now, new HeavyAppProcessEvidence(52, "Stardewish", gamePath, 200 * Mb, now.AddMinutes(-10), false, false, 0, false));
        var gameResult = Assert.Single(HeavyAppClassifier.Classify(game, new HeavyAppDetectionSettings()).Detected);
        Assert.Equal("heavyApp", gameResult.Kind);
        Assert.Equal("gameInstallPath", gameResult.Reason);
    }

    private static HeavyAppEvidenceSnapshot Snapshot(DateTime now, HeavyAppProcessEvidence process)
        => new(now, new[] { process }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
