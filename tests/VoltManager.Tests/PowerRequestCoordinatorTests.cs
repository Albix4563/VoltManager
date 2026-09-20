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
}
