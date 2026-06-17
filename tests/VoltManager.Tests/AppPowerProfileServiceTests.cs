using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class AppPowerProfileServiceTests
{
    [Fact]
    public void PickTargetPlan_UsesHighestPerformancePlan()
    {
        var rules = new[]
        {
            new AppPowerProfileRule { Enabled = true, TargetPlan = PlanId.PowerSaver },
            new AppPowerProfileRule { Enabled = true, TargetPlan = PlanId.Performance },
            new AppPowerProfileRule { Enabled = true, TargetPlan = PlanId.Balanced },
        };

        Assert.Equal(PlanId.Performance, AppPowerProfileService.PickTargetPlan(rules));
    }

    [Fact]
    public void PickTargetPlan_IgnoresDisabledRules()
    {
        var rules = new[]
        {
            new AppPowerProfileRule { Enabled = false, TargetPlan = PlanId.Performance },
            new AppPowerProfileRule { Enabled = true, TargetPlan = PlanId.Balanced },
        };

        Assert.Equal(PlanId.Balanced, AppPowerProfileService.PickTargetPlan(rules));
    }

    [Fact]
    public void PickTargetPlan_ReturnsNullWhenNoEnabledRules()
    {
        var rules = new[]
        {
            new AppPowerProfileRule { Enabled = false, TargetPlan = PlanId.Performance },
        };

        Assert.Null(AppPowerProfileService.PickTargetPlan(rules));
    }

    [Fact]
    public void NormalizePath_TrimsQuotesAndWhitespace()
    {
        Assert.Equal(@"C:\Apps\nike.exe", AppPowerProfileService.NormalizePath("  \"C:\\Apps\\nike.exe\"  "));
    }
}
