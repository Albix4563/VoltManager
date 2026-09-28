using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class AppPowerProfileServiceTests
{
    [Fact]
    public void State_summary_uses_all_unique_profiles_before_truncating_ui_list()
    {
        var detected = Enumerable.Range(0, 9)
            .Select(index => new DetectedAppPowerProfile
            {
                RuleId = $"rule-{index}",
                ProcessId = index + 1,
                Name = ((char)('A' + index)).ToString(),
                Path = $@"C:\Apps\App{index}.exe",
                TargetPlan = PlanId.Balanced,
                KeepAwake = index == 8,
            })
            .ToList();

        var state = AppPowerProfileService.BuildState(true, detected, DateTime.UnixEpoch);

        Assert.True(state.Active);
        Assert.Equal(PlanId.Balanced, state.TargetPlan);
        Assert.True(state.KeepAwakeRequested);
        Assert.Equal(8, state.ActiveProfiles.Count);
        Assert.DoesNotContain(state.ActiveProfiles, profile => profile.KeepAwake);
    }
}
