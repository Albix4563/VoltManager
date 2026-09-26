using System.ComponentModel;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerPlanServiceTests
{
    private const string ExtraBalanced1 = "11111111-1111-4111-8111-111111111111";
    private const string ExtraBalanced2 = "22222222-2222-4222-8222-222222222222";
    private const string ExtraBalanced3 = "33333333-3333-4333-8333-333333333333";
    private const string OemGuid = "44444444-4444-4444-8444-444444444444";
    private const string MappedSaverGuid = "55555555-5555-4555-8555-555555555555";
    private const string SaverCopyGuid = "66666666-6666-4666-8666-666666666666";

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

    [Fact]
    public void FindExtraPlans_Lists_only_copies_of_main_plans_and_ignores_oem()
    {
        string output = string.Join('\n',
        [
            PlanLine(PowerPlanService.SaverGuid, "Power saver"),
            PlanLine(PowerPlanService.BalancedGuid, "Balanced", active: true),
            PlanLine(PowerPlanService.PerformanceGuid, "High performance"),
            PlanLine(ExtraBalanced1, "Balanced"),
            PlanLine(ExtraBalanced2, "Balanced"),
            PlanLine(ExtraBalanced3, "Balanced"),
            PlanLine(OemGuid, "OEM Quiet"),
        ]);
        var service = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(PowerPlanService.BalancedGuid),
            args => args == "/list" ? output : "");

        ExtraPlansReport report = service.FindExtraPlans();

        Assert.True(report.HasExtras);
        Assert.Equal(3, report.Keep.Count);
        Assert.Equal(3, report.Extras.Count);
        Assert.All(report.Extras, plan => Assert.Equal("Balanced", plan.DuplicateOf));
        Assert.DoesNotContain(report.Extras, plan => plan.Guid == OemGuid);
    }

    [Fact]
    public void DeleteExtraPlans_NeverDeletes_canonical_or_kept_guids()
    {
        var calls = new List<string>();
        string output = string.Join('\n',
        [
            PlanLine(PowerPlanService.SaverGuid, "Power saver"),
            PlanLine(PowerPlanService.BalancedGuid, "Balanced", active: true),
            PlanLine(PowerPlanService.PerformanceGuid, "High performance"),
        ]);
        var service = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(PowerPlanService.BalancedGuid),
            args =>
            {
                calls.Add(args);
                return args == "/list" ? output : "";
            });

        DeleteExtraPlansResult result = service.DeleteExtraPlans(
            [PowerPlanService.SaverGuid, PowerPlanService.BalancedGuid, PowerPlanService.PerformanceGuid]);

        Assert.False(result.Success);
        Assert.Empty(result.Deleted);
        Assert.Equal(3, result.Failed.Count);
        Assert.DoesNotContain(calls, call => call.StartsWith("-delete ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeleteExtraPlans_Switches_active_extra_to_balanced_before_delete()
    {
        var installed = new List<(string Guid, string Name)>
        {
            (PowerPlanService.SaverGuid, "Power saver"),
            (PowerPlanService.BalancedGuid, "Balanced"),
            (PowerPlanService.PerformanceGuid, "High performance"),
            (ExtraBalanced1, "Balanced"),
        };
        string activeGuid = ExtraBalanced1;
        var calls = new List<string>();

        string RunPowercfg(string args)
        {
            calls.Add(args);
            if (args == "/list")
                return string.Join('\n', installed.Select(plan =>
                    PlanLine(plan.Guid, plan.Name, plan.Guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase))));
            if (args == "/getactivescheme")
            {
                var active = installed.Single(plan => plan.Guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase));
                return PlanLine(active.Guid, active.Name, active: true);
            }
            if (args.StartsWith("/setactive ", StringComparison.OrdinalIgnoreCase))
            {
                activeGuid = args["/setactive ".Length..];
                return "";
            }
            if (args.StartsWith("-delete ", StringComparison.OrdinalIgnoreCase))
            {
                string guid = args["-delete ".Length..];
                installed.RemoveAll(plan => plan.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));
                return "";
            }
            throw new InvalidOperationException($"Unexpected powercfg call: {args}");
        }

        var service = new PowerPlanService(
            TestSettings.Create(),
            () => Guid.Parse(activeGuid),
            RunPowercfg);

        DeleteExtraPlansResult result = service.DeleteExtraPlans([ExtraBalanced1]);

        Assert.True(result.Success);
        Assert.Contains(ExtraBalanced1, result.Deleted);
        int switchIndex = calls.IndexOf($"/setactive {PowerPlanService.BalancedGuid}");
        int deleteIndex = calls.IndexOf($"-delete {ExtraBalanced1}");
        Assert.True(switchIndex >= 0);
        Assert.True(deleteIndex > switchIndex);
    }

    [Fact]
    public void DeleteExtraPlans_Switches_active_duplicate_to_its_matching_main_plan()
    {
        const string performanceCopy = "3f1d6f1e-4c3a-4b8e-9c55-2a7a4f0b9d11";
        var installed = new List<string>
        {
            PowerPlanService.SaverGuid, PowerPlanService.BalancedGuid, PowerPlanService.PerformanceGuid, performanceCopy,
        };
        string Name(string guid) => guid == PowerPlanService.SaverGuid ? "Power saver"
            : guid == PowerPlanService.BalancedGuid ? "Balanced" : "High performance";
        string activeGuid = performanceCopy;
        var calls = new List<string>();

        string RunPowercfg(string args)
        {
            calls.Add(args);
            if (args == "/list")
                return string.Join('\n', installed.Select(guid => PlanLine(guid, Name(guid), guid == activeGuid)));
            if (args.StartsWith("/setactive ", StringComparison.OrdinalIgnoreCase))
            {
                activeGuid = args["/setactive ".Length..];
                return "";
            }
            if (args.StartsWith("-delete ", StringComparison.OrdinalIgnoreCase))
            {
                installed.Remove(args["-delete ".Length..]);
                return "";
            }
            throw new InvalidOperationException($"Unexpected powercfg call: {args}");
        }

        var service = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(activeGuid), RunPowercfg);

        DeleteExtraPlansResult result = service.DeleteExtraPlans([performanceCopy]);

        Assert.True(result.Success);
        Assert.Contains($"/setactive {PowerPlanService.PerformanceGuid}", calls);
        Assert.DoesNotContain($"/setactive {PowerPlanService.BalancedGuid}", calls);
    }

    [Fact]
    public void FindExtraPlans_Uses_mapped_plan_when_canonical_missing_and_flags_same_named_copy()
    {
        var settings = TestSettings.Create();
        settings.Update(state => state.PlanGuidMap["PowerSaver"] = MappedSaverGuid);
        string output = string.Join('\n',
        [
            PlanLine(MappedSaverGuid, "Power saver"),
            PlanLine(SaverCopyGuid, "Power saver"),
            PlanLine(PowerPlanService.BalancedGuid, "Balanced"),
            PlanLine(PowerPlanService.PerformanceGuid, "High performance"),
        ]);
        var service = new PowerPlanService(settings, () => Guid.Parse(PowerPlanService.BalancedGuid),
            args => args == "/list" ? output : "");

        ExtraPlansReport report = service.FindExtraPlans();

        KeptPowerPlan saver = report.Keep.Single(plan => plan.PlanId == "PowerSaver");
        Assert.Equal(MappedSaverGuid, saver.Guid);
        ExtraPowerPlan copy = report.Extras.Single(plan => plan.Guid == SaverCopyGuid);
        Assert.True(copy.IsDuplicate);
        Assert.Equal("PowerSaver", copy.DuplicateOf);
    }

    [Fact]
    public void DeleteExtraPlans_Removes_plan_guid_map_entry_pointing_to_deleted_guid()
    {
        var settings = TestSettings.Create();
        settings.Update(state => state.PlanGuidMap["PowerSaver"] = ExtraBalanced1);
        var installed = new List<(string Guid, string Name)>
        {
            (PowerPlanService.SaverGuid, "Power saver"),
            (PowerPlanService.BalancedGuid, "Balanced"),
            (PowerPlanService.PerformanceGuid, "High performance"),
            (ExtraBalanced1, "Power saver"),
        };

        string RunPowercfg(string args)
        {
            if (args == "/list")
                return string.Join('\n', installed.Select(plan => PlanLine(plan.Guid, plan.Name)));
            if (args == $"-delete {ExtraBalanced1}")
            {
                installed.RemoveAll(plan => plan.Guid == ExtraBalanced1);
                return "";
            }
            throw new InvalidOperationException($"Unexpected powercfg call: {args}");
        }

        var service = new PowerPlanService(settings, () => null, RunPowercfg);
        DeleteExtraPlansResult result = service.DeleteExtraPlans([ExtraBalanced1]);

        Assert.True(result.Success);
        Assert.False(settings.Current.PlanGuidMap.ContainsKey("PowerSaver"));
    }

    [Fact]
    public void FindExtraPlans_ShouldPrompt_only_for_undismissed_extras()
    {
        var installed = new List<(string Guid, string Name)>
        {
            (PowerPlanService.SaverGuid, "Power saver"),
            (PowerPlanService.BalancedGuid, "Balanced"),
            (PowerPlanService.PerformanceGuid, "High performance"),
            (ExtraBalanced1, "Balanced"),
        };
        var service = new PowerPlanService(
            TestSettings.Create(),
            () => null,
            args => args == "/list"
                ? string.Join('\n', installed.Select(plan => PlanLine(plan.Guid, plan.Name)))
                : "");

        Assert.True(service.FindExtraPlans().ShouldPrompt);

        service.DismissExtraPlans([ExtraBalanced1.ToUpperInvariant()]);
        Assert.False(service.FindExtraPlans().ShouldPrompt);

        installed.Add((OemGuid, "OEM Quiet"));
        Assert.False(service.FindExtraPlans().ShouldPrompt);

        installed.Add((ExtraBalanced2, "Balanced"));
        Assert.True(service.FindExtraPlans().ShouldPrompt);
    }

    private static string PlanLine(string guid, string name, bool active = false)
        => $"Power Scheme GUID: {guid}  ({name}){(active ? " *" : "")}";
}
