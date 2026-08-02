using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class GameConfidenceScorerTests
{
    [Fact]
    public void Score_caps_correlated_groups_and_applies_penalties()
    {
        var assessment = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("manifest", "provenance", 30, "Manifest executable"),
            new GameDetectionEvidence("installRoot", "provenance", 20, "Install root"),
            new GameDetectionEvidence("foreground", "runtime", 20, "Foreground"),
            new GameDetectionEvidence("deny", "penalty", -10, "Known helper"),
        }, "manifest");

        Assert.Equal(45, assessment.Score);
        Assert.Equal("unknown", assessment.Level);
        Assert.Equal("manifest", assessment.PrimaryReason);
        Assert.Equal(4, assessment.Evidence.Count);
    }

    [Theory]
    [InlineData(39, "ignored")]
    [InlineData(40, "unknown")]
    [InlineData(59, "unknown")]
    [InlineData(60, "probable")]
    [InlineData(74, "probable")]
    [InlineData(75, "confirmed")]
    public void Level_uses_documented_thresholds(int score, string expected)
        => Assert.Equal(expected, GameConfidenceScorer.LevelFor(score));

    [Fact]
    public void Score_clamps_penalties_and_large_positive_totals()
    {
        var negative = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("deny", "penalty", -200, "Explicit exclusion"),
        });
        var positive = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("identity", "identity", 100, "Known build"),
            new GameDetectionEvidence("manifest", "provenance", 100, "Manifest"),
            new GameDetectionEvidence("runtime", "runtime", 100, "Runtime"),
            new GameDetectionEvidence("history", "history", 100, "Confirmed before"),
        });

        Assert.Equal(0, negative.Score);
        Assert.Equal(100, positive.Score);
    }

    [Fact]
    public void Score_handles_integer_extremes_before_clamping()
    {
        var positive = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("one", "identity", int.MaxValue, "Large signal"),
            new GameDetectionEvidence("two", "identity", int.MaxValue, "Large signal"),
        });
        var negative = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("one", "penalty", int.MinValue, "Large penalty"),
            new GameDetectionEvidence("two", "penalty", int.MinValue, "Large penalty"),
        });

        Assert.Equal(50, positive.Score);
        Assert.Equal(0, negative.Score);
    }
}
