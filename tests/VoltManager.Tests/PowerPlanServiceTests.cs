using System.ComponentModel;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerPlanServiceTests
{
    [Fact]
    public void GetActivePlan_UsesNativeGuidAndResolvesMappedPlan()
    {
        var settings = new SettingsService();
        const string customGuid = "906662eb-8c87-46e1-9ff1-9548cb110d77";
        settings.Current.PlanGuidMap["PowerSaver"] = customGuid;
        var service = new PowerPlanService(
            settings,
            () => Guid.Parse(customGuid),
            _ => throw new InvalidOperationException("Known plans do not need a friendly-name query."));

        var plan = service.GetActivePlan();

        Assert.NotNull(plan);
        Assert.Equal(customGuid, plan.Guid);
        Assert.Equal(Models.PlanId.PowerSaver, plan.PlanId);
        Assert.True(plan.IsActive);
    }

    [Fact]
    public void GetActivePlan_UsesPowercfgOnlyForUnknownFriendlyName()
    {
        var settings = new SettingsService();
        const string customGuid = "7ac7ce31-fbb1-4ab6-859d-9a74517dfcd4";
        var service = new PowerPlanService(
            settings,
            () => Guid.Parse(customGuid),
            _ => $"Power Scheme GUID: {customGuid}  (OEM Quiet)");

        var plan = service.GetActivePlan();

        Assert.NotNull(plan);
        Assert.Equal(customGuid, plan.Guid);
        Assert.Equal("OEM Quiet", plan.Name);
        Assert.Null(plan.PlanId);
    }

    [Fact]
    public void GetActivePlan_NativeReadFailureDegradesToNoPlan()
    {
        var service = new PowerPlanService(
            new SettingsService(),
            () => throw new Win32Exception(5),
            _ => "");

        Assert.Null(service.GetActivePlan());
    }
}
