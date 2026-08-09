using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanExternalConflictDetectorTests
{
    [Fact]
    public void Fan_capable_process_is_possible_evidence_but_blocks_coexistence_writes()
    {
        var notices = new FanExternalConflictDetector().DetectFromProcessNames(new[]
        {
            "FanControl",
            "ArmouryCrate.UserSessionHelper",
            "notepad",
        });

        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice =>
        {
            Assert.Equal(FanConflictConfidence.Possible, notice.Confidence);
            Assert.True(notice.BlocksControl);
        });
    }

    [Fact]
    public void Process_plus_matching_service_raises_confidence_without_claiming_header_ownership()
    {
        var notices = new FanExternalConflictDetector().DetectFromEvidenceForTests(
            new[] { "ArmouryCrate.UserSessionHelper" },
            new[] { "ArmouryCrateService" });

        var notice = Assert.Single(notices);
        Assert.Equal(FanConflictConfidence.High, notice.Confidence);
        Assert.True(notice.BlocksControl);
        Assert.NotNull(notice.ServiceName);
        Assert.DoesNotContain("confirmed", notice.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rgb_only_process_is_informational_by_default()
    {
        var notice = Assert.Single(new FanExternalConflictDetector().DetectFromProcessNames(new[] { "OpenRGB" }));
        Assert.False(notice.BlocksControl);
    }
}
