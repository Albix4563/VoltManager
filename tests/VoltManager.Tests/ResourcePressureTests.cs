using VoltManager.Models;
using VoltManager.Performance;

namespace VoltManager.Tests;

public sealed class ResourcePressureTests
{
    private static MetricsSnapshot Metrics(
        double cpu = 30,
        double gpu = 20,
        double ram = 50,
        double ramTotal = 32,
        bool gpuAvailable = true)
        => new()
        {
            Cpu = cpu,
            Gpu = gpu,
            GpuAvailable = gpuAvailable,
            RamPct = ram,
            RamTotalGb = ramTotal,
        };

    [Fact]
    public void IdleCapableMachine_UsesFullProfile()
    {
        var coordinator = new ResourcePressureCoordinator(logicalCores: 8);
        var state = coordinator.Observe(Metrics(), gameActive: false, DateTime.UnixEpoch);
        Assert.Equal(ResourceProfile.Full, state.Profile);
        Assert.Equal("normal", state.Reason);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(32, 4)]
    public void ConstrainedHardware_UsesBalancedProfile(double ramTotal, int cores)
    {
        var coordinator = new ResourcePressureCoordinator(logicalCores: cores);
        var state = coordinator.Observe(Metrics(ramTotal: ramTotal), false, DateTime.UnixEpoch);
        Assert.Equal(ResourceProfile.Balanced, state.Profile);
        Assert.Equal("hardware_tier", state.Reason);
    }

    [Fact]
    public void GameDetection_EntersGamingImmediately()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var state = coordinator.Observe(Metrics(), true, DateTime.UnixEpoch);
        Assert.Equal(ResourceProfile.Gaming, state.Profile);
        Assert.True(state.GameActive);
    }

    [Fact]
    public void SingleCpuSpike_DoesNotEnterCritical()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var t0 = DateTime.UnixEpoch;
        Assert.Equal(ResourceProfile.Gaming, coordinator.Observe(Metrics(cpu: 98), true, t0).Profile);
        Assert.Equal(ResourceProfile.Gaming, coordinator.Observe(Metrics(cpu: 40), true, t0.AddSeconds(1)).Profile);
        Assert.Equal(ResourceProfile.Gaming, coordinator.Observe(Metrics(cpu: 98), true, t0.AddSeconds(2)).Profile);
    }

    [Fact]
    public void SustainedGameLoad_EntersCriticalAfterHysteresis()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var t0 = DateTime.UnixEpoch;
        Assert.Equal(ResourceProfile.Gaming, coordinator.Observe(Metrics(cpu: 98), true, t0).Profile);
        Assert.Equal(ResourceProfile.Gaming, coordinator.Observe(Metrics(cpu: 98), true, t0.AddSeconds(4)).Profile);
        var state = coordinator.Observe(Metrics(cpu: 98), true, t0.AddSeconds(5));
        Assert.Equal(ResourceProfile.Critical, state.Profile);
        Assert.Equal("game_load", state.Reason);
    }

    [Fact]
    public void CriticalLoad_RequiresClearWindowBeforeRecovery()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var t0 = DateTime.UnixEpoch;
        coordinator.Observe(Metrics(cpu: 98), true, t0);
        coordinator.Observe(Metrics(cpu: 98), true, t0.AddSeconds(5));

        Assert.Equal(ResourceProfile.Critical,
            coordinator.Observe(Metrics(cpu: 30), true, t0.AddSeconds(6)).Profile);
        Assert.Equal(ResourceProfile.Critical,
            coordinator.Observe(Metrics(cpu: 30), true, t0.AddSeconds(20)).Profile);
        Assert.Equal(ResourceProfile.Gaming,
            coordinator.Observe(Metrics(cpu: 30), true, t0.AddSeconds(21)).Profile);
    }

    [Fact]
    public void MemoryPressure_EntersCriticalImmediately_AndUsesLowerExitThreshold()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var t0 = DateTime.UnixEpoch;
        Assert.Equal(ResourceProfile.Critical,
            coordinator.Observe(Metrics(ram: 93), false, t0).Profile);

        Assert.Equal(ResourceProfile.Critical,
            coordinator.Observe(Metrics(ram: 88), false, t0.AddSeconds(20)).Profile);
        Assert.Equal(ResourceProfile.Critical,
            coordinator.Observe(Metrics(ram: 80), false, t0.AddSeconds(21)).Profile);
        Assert.Equal(ResourceProfile.Full,
            coordinator.Observe(Metrics(ram: 80), false, t0.AddSeconds(36)).Profile);
    }

    [Fact]
    public void GameExitCooldown_PreventsProfileFlapping()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        var t0 = DateTime.UnixEpoch;
        coordinator.Observe(Metrics(), true, t0);
        Assert.Equal(ResourceProfile.Gaming,
            coordinator.Observe(Metrics(), false, t0.AddSeconds(10)).Profile);
        Assert.Equal(ResourceProfile.Full,
            coordinator.Observe(Metrics(), false, t0.AddSeconds(16)).Profile);
    }

    [Fact]
    public void UiVisibility_IsStateOnly_AndDoesNotChangeSafetyProfile()
    {
        var coordinator = new ResourcePressureCoordinator(8);
        coordinator.Observe(Metrics(ram: 93), false, DateTime.UnixEpoch);
        var hidden = coordinator.SetUiVisible(false, DateTime.UnixEpoch.AddSeconds(1));
        Assert.False(hidden.UiVisible);
        Assert.Equal(ResourceProfile.Critical, hidden.Profile);
    }

    [Theory]
    [InlineData(ResourceProfile.Full, 1, true, true)]
    [InlineData(ResourceProfile.Balanced, 2, true, true)]
    [InlineData(ResourceProfile.Gaming, 3, true, true)]
    [InlineData(ResourceProfile.Critical, 5, true, false)]
    public void WebViewPolicy_MapsProfilesToElasticCadence(
        ResourceProfile profile,
        int expectedSeconds,
        bool publish,
        bool allowProcessPolling)
    {
        var plan = new WebViewResourceController().Resolve(profile, visible: true);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), plan.MetricsInterval);
        Assert.Equal(publish, plan.PublishMetrics);
        Assert.Equal(allowProcessPolling, plan.AllowProcessPolling);
        Assert.True(plan.LowMemoryTarget);
        Assert.False(plan.SuspendRenderer);
    }

    [Fact]
    public void HiddenWebView_PublishesNothingAndRequestsSuspend()
    {
        var plan = new WebViewResourceController().Resolve(ResourceProfile.Full, visible: false);
        Assert.False(plan.PublishMetrics);
        Assert.False(plan.AllowProcessPolling);
        Assert.True(plan.SuspendRenderer);
        Assert.True(plan.LowMemoryTarget);
    }

    [Fact]
    public void UiPublisher_CoalescesToLatestValueAtConfiguredCadence()
    {
        var publisher = new UiMetricsPublisher();
        var plan = new WebViewResourceController().Resolve(ResourceProfile.Gaming, visible: true);
        var t0 = DateTime.UnixEpoch;

        Assert.True(publisher.TryTake(Metrics(cpu: 10), plan, t0, out var first));
        Assert.Equal(10, first!.Cpu);
        Assert.False(publisher.TryTake(Metrics(cpu: 20), plan, t0.AddSeconds(1), out _));
        Assert.False(publisher.TryTake(Metrics(cpu: 30), plan, t0.AddSeconds(2), out _));
        Assert.True(publisher.TryTake(Metrics(cpu: 40), plan, t0.AddSeconds(3), out var latest));
        Assert.Equal(40, latest!.Cpu);
    }

    [Fact]
    public void UiPublisher_HiddenPlanNeverPublishes_ThenResetPublishesImmediately()
    {
        var publisher = new UiMetricsPublisher();
        var controller = new WebViewResourceController();
        var t0 = DateTime.UnixEpoch;

        Assert.False(publisher.TryTake(Metrics(cpu: 10),
            controller.Resolve(ResourceProfile.Full, false), t0, out _));
        publisher.ResetCadence();
        Assert.True(publisher.TryTake(Metrics(cpu: 20),
            controller.Resolve(ResourceProfile.Full, true), t0.AddSeconds(1), out var visible));
        Assert.Equal(20, visible!.Cpu);
    }
}
