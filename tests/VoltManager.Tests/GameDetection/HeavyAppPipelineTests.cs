using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public sealed class HeavyAppPipelineTests
{
    [Fact]
    public void Pipeline_exposes_separate_evidence_classifier_and_tracker_components()
    {
        Assert.NotNull(typeof(HeavyAppEvidenceCollector));
        Assert.NotNull(typeof(HeavyAppClassifier));
        Assert.NotNull(typeof(HeavyAppTracker));
    }
}
