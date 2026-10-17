using System.Reflection;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerRequestCoordinatorTests
{
    [Theory]
    [InlineData("power", "power")]
    [InlineData("thermal", "power,thermal")]
    [InlineData("idle", "power,thermal,idle")]
    [InlineData("profile", "power,thermal,idle,profile")]
    [InlineData("heavy", "power,thermal,idle,profile,heavy")]
    [InlineData("none", "power,thermal,idle,profile,heavy,cpu")]
    public void ProcessMetrics_preserves_priority(string blocker, string expected)
    {
        var calls = new List<string>();
        bool Hit(string name) { calls.Add(name); return blocker == name; }

        using var coordinator = PowerRequestCoordinator.ForPolicyTest(new PowerPolicyPipeline(
            _ => Hit("power"),
            (_, _) => Hit("thermal"),
            _ => Hit("idle"),
            _ => Hit("profile"),
            _ => Hit("heavy"),
            (_, _) => calls.Add("cpu")));

        coordinator.Start();
        coordinator.ProcessMetrics(new MetricsSnapshot(), DateTime.UnixEpoch);

        Assert.Equal(expected, string.Join(",", calls));
    }

    [Fact]
    public void Cpu_automation_publication_ignores_timestamp_and_sub_decimal_jitter()
    {
        var settings = TestSettings.Create();
        var power = new PowerPlanService(settings, () => Guid.Parse(PowerPlanService.BalancedGuid), _ => "");
        using var awake = new PowerAwakeService(settings, () => false, () => DateTime.UnixEpoch);
        using var profiles = new AppPowerProfileService(settings);
        using var heavy = new HeavyAppDetectionService(settings);
        using var coordinator = new PowerRequestCoordinator(
            settings, power, awake, new AutomationEngine(), profiles, heavy,
            new PowerSourcePlanService(settings, () => new PowerSourceSnapshot(true, 100)),
            new ThermalGuardService(settings),
            new IdlePowerGuardService(settings, () => 0, () => false), () => null);
        int published = 0;
        coordinator.CpuAutomationStateChanged += _ => published++;
        coordinator.Start();
        int afterStart = published;

        coordinator.ProcessMetrics(new MetricsSnapshot { Cpu = 0 }, DateTime.UnixEpoch);
        int afterFirst = published;
        coordinator.ProcessMetrics(new MetricsSnapshot { Cpu = 0 }, DateTime.UnixEpoch.AddSeconds(1));

        Assert.Equal(afterFirst, published);
        Assert.True(afterFirst >= afterStart);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1), coordinator.CpuAutomationState.SampledAtUtc);

        settings.Update(state => state.MasterAutomationEnabled = !state.MasterAutomationEnabled);
        coordinator.ProcessMetrics(new MetricsSnapshot { Cpu = 0 }, DateTime.UnixEpoch.AddSeconds(2));
        Assert.Equal(afterFirst + 1, published);
        coordinator.ProcessMetrics(new MetricsSnapshot { Cpu = 0 }, DateTime.UnixEpoch.AddSeconds(3));
        Assert.Equal(afterFirst + 1, published);
    }

    [Fact]
    public void Manual_override_updates_settings_active_plan_and_publication()
    {
        var settings = TestSettings.Create();
        Guid activeGuid = Guid.Parse(PowerPlanService.BalancedGuid);
        string RunPowercfg(string args)
        {
            if (args.StartsWith("/setactive ", StringComparison.OrdinalIgnoreCase))
                activeGuid = Guid.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            return "";
        }

        var power = new PowerPlanService(settings, () => activeGuid, RunPowercfg);
        using var awake = new PowerAwakeService(settings, () => false, () => DateTime.UnixEpoch);
        using var profiles = new AppPowerProfileService(settings);
        using var heavy = new HeavyAppDetectionService(settings);
        using var coordinator = new PowerRequestCoordinator(
            settings,
            power,
            awake,
            new AutomationEngine(),
            profiles,
            heavy,
            new PowerSourcePlanService(settings, () => new PowerSourceSnapshot(true, 100)),
            new ThermalGuardService(settings),
            new IdlePowerGuardService(settings, () => 0, () => false),
            () => null);

        PowerPlan? published = null;
        coordinator.ActivePlanChanged += plan => published = plan;
        coordinator.Start();

        Assert.True(coordinator.SetManualOverride(PlanId.Performance, null));
        Assert.Equal("performance", settings.Current.Override?.Plan);
        Assert.Equal(PlanId.Performance, coordinator.ActivePlan?.PlanId);
        Assert.Equal(PlanId.Performance, published?.PlanId);
        Assert.True(coordinator.CpuAutomationState.ManualOverrideActive);
    }

    [Fact]
    public void Failed_manual_override_preserves_heavy_app_session_restore_plan()
    {
        var settings = TestSettings.Create();
        Guid activeGuid = Guid.Parse(PowerPlanService.PerformanceGuid);
        string RunPowercfg(string args)
        {
            if (!args.StartsWith("/setactive ", StringComparison.OrdinalIgnoreCase))
                return "";

            Guid requested = Guid.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            if (requested != Guid.Parse(PowerPlanService.SaverGuid))
                activeGuid = requested;
            return "";
        }

        var power = new PowerPlanService(settings, () => activeGuid, RunPowercfg);
        using var awake = new PowerAwakeService(settings, () => false, () => DateTime.UnixEpoch);
        using var profiles = new AppPowerProfileService(settings);
        using var heavy = new HeavyAppDetectionService(settings);
        using var coordinator = new PowerRequestCoordinator(
            settings,
            power,
            awake,
            new AutomationEngine(),
            profiles,
            heavy,
            new PowerSourcePlanService(settings, () => new PowerSourceSnapshot(true, 100)),
            new ThermalGuardService(settings),
            new IdlePowerGuardService(settings, () => 0, () => false),
            () => null);

        coordinator.Start();
        coordinator.RefreshActivePlanFromSystem(DateTime.UnixEpoch);
        SetField(coordinator, "_heavyAppPlanSessionActive", true);
        SetField(coordinator, "_planBeforeHeavyAppSession", (PlanId?)PlanId.Balanced);

        Assert.False(coordinator.SetManualOverride(PlanId.PowerSaver, null));
        Assert.Equal(PlanId.Balanced, GetField<PlanId?>(coordinator, "_planBeforeHeavyAppSession"));

        MethodInfo endSession = typeof(PowerRequestCoordinator).GetMethod(
            "HandleHeavyAppDetection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)endSession.Invoke(coordinator, [DateTime.UnixEpoch.AddMinutes(1)])!);
        Assert.Equal(PlanId.Balanced, coordinator.ActivePlan?.PlanId);
    }

    [Fact]
    public void Power_coordinator_dispose_is_terminal()
    {
        using var coordinator = PowerRequestCoordinator.ForPolicyTest(
            new PowerPolicyPipeline(_ => false, (_, _) => false, _ => false,
                _ => false, _ => false, (_, _) => { }));

        coordinator.Start();
        coordinator.Stop();
        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(() => coordinator.Start());
    }

    private static void SetField<T>(PowerRequestCoordinator coordinator, string name, T value)
        => typeof(PowerRequestCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, value);

    private static T GetField<T>(PowerRequestCoordinator coordinator, string name)
        => (T)typeof(PowerRequestCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
}
