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

    // Calibration scenarios: the score must land on the intended side of the game gate.
    public static TheoryData<string, GameDetectionEvidence[], int, bool> CalibrationScenarios => new()
    {
        {
            "Steam game mid-match, exclusive fullscreen",
            new[]
            {
                new GameDetectionEvidence("gameInstallPath", "provenance", 30, "Steam library"),
                new GameDetectionEvidence("gpu3dSustained", "runtime", 25, "GPU 3D busy"),
                new GameDetectionEvidence("d3dFullscreen", "runtime", 20, "D3D fullscreen"),
                new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
                new GameDetectionEvidence("resourceHeuristic", "runtime", 5, "Large working set"),
                new GameDetectionEvidence("duration15s", "runtime", 4, "Alive 15s"),
                new GameDetectionEvidence("duration2m", "runtime", 3, "Alive 2m"),
            },
            90, true
        },
        {
            "Steam game just launched, still loading",
            new[]
            {
                new GameDetectionEvidence("gameInstallPath", "provenance", 30, "Steam library"),
                new GameDetectionEvidence("gpu3dSustained", "runtime", 25, "GPU 3D busy"),
                new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
            },
            70, true
        },
        {
            "Indie game in a custom folder, fullscreen",
            new[]
            {
                new GameDetectionEvidence("gpu3dSustained", "runtime", 25, "GPU 3D busy"),
                new GameDetectionEvidence("d3dFullscreen", "runtime", 20, "D3D fullscreen"),
                new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
                new GameDetectionEvidence("duration15s", "runtime", 4, "Alive 15s"),
                new GameDetectionEvidence("duration2m", "runtime", 3, "Alive 2m"),
            },
            60, true
        },
        {
            "Blender rendering in a window",
            new[]
            {
                new GameDetectionEvidence("gpu3dSustained", "runtime", 25, "GPU 3D busy"),
                new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
                new GameDetectionEvidence("resourceHeuristic", "runtime", 5, "Large working set"),
                new GameDetectionEvidence("duration15s", "runtime", 4, "Alive 15s"),
                new GameDetectionEvidence("duration2m", "runtime", 3, "Alive 2m"),
            },
            52, false
        },
        {
            "Electron app maximized, 800 MB",
            new[]
            {
                new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
                new GameDetectionEvidence("duration15s", "runtime", 4, "Alive 15s"),
                new GameDetectionEvidence("duration2m", "runtime", 3, "Alive 2m"),
            },
            22, false
        },
    };

    [Theory]
    [MemberData(nameof(CalibrationScenarios))]
    public void Score_puts_calibration_scenarios_on_the_right_side_of_the_gate(
        string scenario, GameDetectionEvidence[] evidence, int expectedScore, bool expectedGame)
    {
        var assessment = GameConfidenceScorer.Score(evidence);

        Assert.Equal(expectedScore, assessment.Score);
        Assert.Equal(expectedGame, assessment.Score >= GameConfidenceScorer.GameThreshold);
        Assert.NotEmpty(scenario);
    }

    [Fact]
    public void Runtime_group_alone_can_reach_the_game_gate()
    {
        // A process holding the 3D engine in exclusive fullscreen is a game regardless
        // of where its binary lives — the case the install-path heuristics cannot see.
        var assessment = GameConfidenceScorer.Score(new[]
        {
            new GameDetectionEvidence("gpu3dSustained", "runtime", 25, "GPU 3D busy"),
            new GameDetectionEvidence("d3dFullscreen", "runtime", 20, "D3D fullscreen"),
            new GameDetectionEvidence("foreground", "runtime", 15, "Foreground"),
            new GameDetectionEvidence("resourceHeuristic", "runtime", 5, "Large working set"),
            new GameDetectionEvidence("duration15s", "runtime", 4, "Alive 15s"),
            new GameDetectionEvidence("duration2m", "runtime", 3, "Alive 2m"),
        });

        Assert.Equal(60, assessment.Score);
        Assert.Equal("probable", assessment.Level);
    }

    [Fact]
    public void GameThreshold_matches_the_probable_level_boundary()
    {
        Assert.Equal("probable", GameConfidenceScorer.LevelFor(GameConfidenceScorer.GameThreshold));
        Assert.Equal("unknown", GameConfidenceScorer.LevelFor(GameConfidenceScorer.GameThreshold - 1));
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
