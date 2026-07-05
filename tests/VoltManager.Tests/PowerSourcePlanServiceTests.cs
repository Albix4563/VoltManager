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
        var service = CreateService(() => Snapshot(plugged));

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
        var service = CreateService(() => Snapshot(plugged));

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
    public void LowBatteryOnDc_TargetsPowerSaverAndBlocksLowerPriority()
    {
        var service = CreateService(() => Snapshot(pluggedIn: false, batteryPercent: 19));

        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
        Assert.True(decision.State.LowBatteryActive);
        Assert.True(decision.State.Active);
        Assert.Equal(19, decision.State.BatteryPercent);
        Assert.Equal(PlanId.Performance, decision.State.SavedPlan);
    }

    [Fact]
    public void LowBatteryOnDc_OverridesManualOverride()
    {
        var service = CreateService(() => Snapshot(pluggedIn: false, batteryPercent: 10));

        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: true);

        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
        Assert.True(decision.State.LowBatteryActive);
        Assert.True(decision.State.ManualOverrideActive);
    }

    [Fact]
    public void ActiveLowBatterySession_StaysLockedWhenBatteryPercentBecomesUnknown()
    {
        int? batteryPercent = 19;
        var service = CreateService(() => Snapshot(pluggedIn: false, batteryPercent));

        service.Evaluate(PlanId.Performance, manualOverrideActive: false);
        batteryPercent = null;
        var decision = service.Evaluate(PlanId.Balanced, manualOverrideActive: false);

        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
        Assert.True(decision.State.LowBatteryActive);
    }

    [Fact]
    public void BatteryAtTwentyPercent_DoesNotTriggerLowBatterySaver()
    {
        var service = CreateService(() => Snapshot(pluggedIn: false, batteryPercent: 20));

        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Null(decision.TargetPlan);
        Assert.False(decision.BlocksLowerPriority);
        Assert.False(decision.State.LowBatteryActive);
    }

    [Fact]
    public void PluggedAfterLowBattery_UsesPluggedPlanAndRestoresPlanBeforeLowBatteryOnUnplug()
    {
        bool? plugged = false;
        int? batteryPercent = 19;
        var service = CreateService(() => Snapshot(plugged, batteryPercent));

        var lowBattery = service.Evaluate(PlanId.Balanced, manualOverrideActive: false);
        Assert.Equal(PlanId.PowerSaver, lowBattery.TargetPlan);

        plugged = true;
        var pluggedDecision = service.Evaluate(PlanId.PowerSaver, manualOverrideActive: false);

        Assert.Equal(PlanId.Performance, pluggedDecision.TargetPlan);
        Assert.False(pluggedDecision.State.LowBatteryActive);
        Assert.True(pluggedDecision.State.Active);
        Assert.Equal(PlanId.Balanced, pluggedDecision.State.SavedPlan);

        plugged = false;
        batteryPercent = 80;
        var unpluggedDecision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Equal(PlanId.Balanced, unpluggedDecision.TargetPlan);
        Assert.False(unpluggedDecision.State.LowBatteryActive);
        Assert.False(unpluggedDecision.State.Active);
    }

    [Fact]
    public void UnknownPowerSource_DoesNotBlockOrTarget()
    {
        var service = CreateService(() => Snapshot(null));

        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.Null(decision.TargetPlan);
        Assert.False(decision.BlocksLowerPriority);
        Assert.False(decision.State.Active);
    }

    [Fact]
    public void NoSystemBattery_ReportsUnknownSource_SoDesktopIsNeverLockedToPluggedPlan()
    {
        // Desktop fisso: ACLineStatus=1 (sempre AC), BatteryFlag=128 (no system battery),
        // BatteryLifePercent=255 (unknown). La feature non deve mai attivarsi.
        var snapshot = PowerSourcePlanService.ToSnapshot(acLineStatus: 1, batteryFlag: 128, batteryLifePercent: 255);

        Assert.Null(snapshot.PluggedIn);
        Assert.Null(snapshot.BatteryPercent);
    }

    [Fact]
    public void LaptopWithBattery_StillReportsPluggedState()
    {
        var onAc = PowerSourcePlanService.ToSnapshot(acLineStatus: 1, batteryFlag: 8, batteryLifePercent: 80);
        var onDc = PowerSourcePlanService.ToSnapshot(acLineStatus: 0, batteryFlag: 1, batteryLifePercent: 55);

        Assert.True(onAc.PluggedIn);
        Assert.Equal(80, onAc.BatteryPercent);
        Assert.False(onDc.PluggedIn);
        Assert.Equal(55, onDc.BatteryPercent);
    }

    [Fact]
    public void DisablingDuringAcSession_RestoresPreviousPlan()
    {
        var settings = new SettingsService(SettingsPath);
        var service = new PowerSourcePlanService(settings, () => Snapshot(true));

        service.Evaluate(PlanId.PowerSaver, manualOverrideActive: false);
        var state = service.SetEnabled(false, manualOverrideActive: false);
        var decision = service.Evaluate(PlanId.Performance, manualOverrideActive: false);

        Assert.False(state.Enabled);
        Assert.Equal(PlanId.PowerSaver, decision.TargetPlan);
        Assert.True(decision.BlocksLowerPriority);
    }

    private PowerSourcePlanService CreateService(bool pluggedIn)
        => CreateService(() => Snapshot(pluggedIn));

    private PowerSourcePlanService CreateService(Func<PowerSourceSnapshot?> reader)
        => new(new SettingsService(SettingsPath), reader);

    private static PowerSourceSnapshot Snapshot(bool? pluggedIn, int? batteryPercent = null)
        => new(pluggedIn, batteryPercent);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
