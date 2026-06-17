using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class PowerSourcePlanServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VoltManagerTests_" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void PluggedIn_SavesPreviousAndTargetsPerformance()
    {
        var service = CreateService(true);

        var decision = service.Evaluate(PlanId.Balanced, manualOverrideActive: false);

        Assert.Equal(PlanId.Performance, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
        Assert.True(decision.State.Active);
        Assert.Equal(PlanId.Balanced, decision.State.SavedPlan);
    }

    [Fact]
    public void Unplugged_RestoresSavedPreviousPlan()
    {
        bool? plugged = true;
        var service = CreateService(() => plugged);

        service.Evaluate(PlanId.PowerSaver, manualOverrideActive: false);
        plugged = false;
        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
        Assert.False(decision.State.Active);
        Assert.Null(decision.State.SavedPlan);
    }

    [Fact]
    public void PluggedInWhenPerformanceAlreadyActive_FallsBackToBalancedOnUnplug()
    {
        bool? plugged = true;
        var service = CreateService(() => plugged);

        service.Evaluate(PlanId.Performance, manualOverrideActive: false);
        plugged = false;
        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Equal(PlanId.Balanced, decision.TargetPlan);
    }

    [Fact]
    public void ManualOverride_BlocksPowerSourceAutomation()
    {
        var service = CreateService(true);

        var decision = service.Evaluate(PlanId.Balanced, manualOverrideActive: true);

        Assert.Null(decision.TargetPlan);
        Assert.False(decision.BlocksLowerPriority);
        Assert.True(decision.State.ManualOverrideActive);
    }

    [Fact]
    public void DisablingDuringAcSession_RestoresPreviousPlan()
    {
        var settings = new SettingsService(SettingsPath);
        var service = new PowerSourcePlanService(settings, () => true);

        service.Evaluate(PlanId.PowerSaver, manualOverrideActive: false);
        var state = service.SetEnabled(false, manualOverrideActive: false);
        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.False(state.Enabled);
        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
    }

    private PowerSourcePlanService CreateService(bool pluggedIn)
        => CreateService(() => pluggedIn);

    private PowerSourcePlanService CreateService(Func<bool?> reader)
        => new(new SettingsService(SettingsPath), reader);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
