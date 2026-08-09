using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanExternalConflictDetectorTests
{
    [Fact]
    public void Process_name_evidence_is_possible_and_never_claims_confirmed_ownership()
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
            Assert.False(notice.BlocksControl);
        });
    }
}
