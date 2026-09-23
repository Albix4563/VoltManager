using System.ComponentModel;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerPlanServiceTests
{
    [Fact]
    public void GetActivePlan_UsesNativeGuidAndResolvesMappedPlan()
    {
        var settings = TestSettings.Create();
        const string customGuid = "906662eb-8c87-46e1-9ff1-9548cb110d77";
        settings.Update(state => state.PlanGuidMap["PowerSaver"] = customGuid);
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
        var settings = TestSettings.Create();
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
            TestSettings.Create(),
            () => throw new Win32Exception(5),
            _ => "");

        Assert.Null(service.GetActivePlan());
    }

    [Fact]
    public void RestoreDefaultPlans_DoesNotDuplicateSuccessfulPlanAgainAfterPartialFailure()
    {
        var settings = TestSettings.Create();
        const string restoredSaverGuid = "58c6c0be-451e-4745-b886-a2b954462a6a";
        var installedGuids = new List<string> { PowerPlanService.BalancedGuid };
        int saverRestoreCount = 0;

        string RunPowercfg(string args)
        {
            if (args == "/list")
                return string.Join('\n', installedGuids.Select(guid => $"Power Scheme GUID: {guid}"));

            if (args == $"-duplicatescheme {PowerPlanService.SaverGuid}")
            {
                saverRestoreCount++;
                installedGuids.Add(restoredSaverGuid);
                return $"Power Scheme GUID: {restoredSaverGuid}";
            }

            if (args == $"-duplicatescheme {PowerPlanService.PerformanceGuid}")
                return "";

            throw new InvalidOperationException($"Unexpected powercfg call: {args}");
        }

        var service = new PowerPlanService(settings, () => null, RunPowercfg);

        Assert.False(service.RestoreDefaultPlans());
        Assert.False(service.RestoreDefaultPlans());

        Assert.Equal(1, saverRestoreCount);
        Assert.Equal(restoredSaverGuid, settings.Current.PlanGuidMap["PowerSaver"]);
    }
}
