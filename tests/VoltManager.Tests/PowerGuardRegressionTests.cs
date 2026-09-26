using System.Globalization;
using System.Reflection;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerGuardRegressionTests
{
    [Fact]
    public void Low_battery_session_inherits_plan_saved_before_ac_session()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.PowerSourcePlan.Enabled = true;
            state.PowerSourcePlan.PluggedPlan = PlanId.Performance;
            state.PowerSourcePlan.LowBatteryThresholdPercent = 30;
        });
        PowerSourceSnapshot source = new(true, 100);
        var service = new PowerSourcePlanService(settings, () => source);

        Assert.Equal(PlanId.Performance, service.Evaluate(PlanId.Balanced, false).TargetPlan);

        source = new PowerSourceSnapshot(false, 20);
        Assert.Equal(PlanId.PowerSaver, service.Evaluate(PlanId.Performance, false).TargetPlan);

        source = new PowerSourceSnapshot(false, 40);
        Assert.Equal(PlanId.Balanced, service.Evaluate(PlanId.PowerSaver, false).TargetPlan);
    }

    [Fact]
    public void Thermal_guard_does_not_restore_balanced_when_user_was_already_on_target()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.ThermalGuard.Enabled = true;
            state.ThermalGuard.TargetPlan = PlanId.PowerSaver;
            state.ThermalGuard.ThresholdCelsius = 80;
            state.ThermalGuard.CoolThresholdCelsius = 70;
            state.ThermalGuard.HoldSeconds = ThermalGuardSettings.MinHoldSeconds;
        });
        var service = new ThermalGuardService(settings);
        DateTime now = DateTime.UnixEpoch;

        Assert.Null(service.Evaluate(90, null, PlanId.PowerSaver, false, true, now).TargetPlan);
        Assert.Null(service.Evaluate(90, null, PlanId.PowerSaver, false, true,
            now.AddSeconds(ThermalGuardSettings.MinHoldSeconds)).TargetPlan);
        Assert.Null(service.Evaluate(60, null, PlanId.PowerSaver, false, true,
            now.AddSeconds(ThermalGuardSettings.MinHoldSeconds + 1)).TargetPlan);
    }

    [Fact]
    public void Thermal_guard_restores_saved_plan_after_being_disabled_mid_session()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.ThermalGuard.Enabled = true;
            state.ThermalGuard.TargetPlan = PlanId.PowerSaver;
            state.ThermalGuard.ThresholdCelsius = 80;
            state.ThermalGuard.CoolThresholdCelsius = 70;
            state.ThermalGuard.HoldSeconds = ThermalGuardSettings.MinHoldSeconds;
        });
        var service = new ThermalGuardService(settings);
        DateTime now = DateTime.UnixEpoch;

        Assert.Null(service.Evaluate(90, null, PlanId.Balanced, false, true, now).TargetPlan);
        Assert.Equal(PlanId.PowerSaver, service.Evaluate(90, null, PlanId.Balanced, false, true,
            now.AddSeconds(ThermalGuardSettings.MinHoldSeconds)).TargetPlan);
        service.SetEnabled(false);

        Assert.Equal(PlanId.Balanced,
            service.Evaluate(90, null, PlanId.PowerSaver, false, true,
                now.AddSeconds(ThermalGuardSettings.MinHoldSeconds + 1)).TargetPlan);
    }

    [Fact]
    public void Idle_guard_does_not_restore_balanced_when_user_was_already_on_target()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.IdlePowerGuard.Enabled = true;
            state.IdlePowerGuard.TargetPlan = PlanId.PowerSaver;
            state.IdlePowerGuard.IdleMinutes = 1;
        });
        uint idleMs = 120_000;
        var service = new IdlePowerGuardService(settings, () => idleMs, () => true);

        Assert.Null(service.Evaluate(PlanId.PowerSaver, false, true).TargetPlan);
        idleMs = 0;
        Assert.Null(service.Evaluate(PlanId.PowerSaver, false, true).TargetPlan);
    }

    [Fact]
    public void Idle_guard_restores_saved_plan_after_being_disabled_mid_session()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.IdlePowerGuard.Enabled = true;
            state.IdlePowerGuard.TargetPlan = PlanId.PowerSaver;
            state.IdlePowerGuard.IdleMinutes = 1;
        });
        var service = new IdlePowerGuardService(settings, () => 120_000, () => true);

        Assert.Equal(PlanId.PowerSaver, service.Evaluate(PlanId.Balanced, false, true).TargetPlan);
        service.SetEnabled(false);

        Assert.Equal(PlanId.Balanced, service.Evaluate(PlanId.PowerSaver, false, true).TargetPlan);
    }

    [Fact]
    public void Scheduled_relative_callback_queued_before_stop_cannot_execute_after_stop()
    {
        SettingsService settings = TestSettings.Create();
        var executor = new RecordingPowerActionExecutor();
        using var service = new ScheduledPowerActionService(settings, executor, new FixedClock());
        service.Start();
        service.ScheduleAfter(TimeSpan.FromMinutes(1), ScheduledPowerActionType.Shutdown);
        long generation = (long)Field(service, "_generation")!;

        service.Stop();
        Invoke(service, "ExecuteRelativeCallback", generation);

        Assert.Empty(executor.Actions);
    }

    [Fact]
    public void Scheduled_daily_callback_queued_before_stop_cannot_execute_after_stop()
    {
        SettingsService settings = TestSettings.Create();
        var executor = new RecordingPowerActionExecutor();
        using var service = new ScheduledPowerActionService(settings, executor, new FixedClock());
        service.Start();
        TimeOnly now = TimeOnly.ParseExact(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), "HH:mm", CultureInfo.InvariantCulture);
        service.ScheduleDaily(now, ScheduledPowerActionType.Restart);
        long generation = (long)Field(service, "_generation")!;

        service.Stop();
        MethodInfo daily = service.GetType().GetMethod("DailyCheckCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        daily.Invoke(service, daily.GetParameters().Length == 0 ? null : new object[] { generation });

        Assert.Empty(executor.Actions);
    }

    private static object? Field(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private static void Invoke(object instance, string name, params object[] args)
        => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private sealed class RecordingPowerActionExecutor : IPowerActionExecutor
    {
        public List<ScheduledPowerActionType> Actions { get; } = new();
        public void Execute(ScheduledPowerActionType action) => Actions.Add(action);
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow => DateTime.UnixEpoch;
    }
}
