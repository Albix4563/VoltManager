using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class BatteryHealthServiceTests
{
    private static BatteryCapacitySnapshot Cap(int? designed, int? full)
        => new() { DesignedCapacityMwh = designed, FullChargedCapacityMwh = full };

    private static BatteryHealthState Health(int? designed, int? full)
        => new BatteryHealthService(() => Cap(designed, full)).GetHealth();

    [Fact]
    public void NewBattery_FullEqualsDesign_IsExcellentHundredPercent()
    {
        var s = Health(50000, 50000);

        Assert.True(s.Available);
        Assert.Equal(100.0, s.HealthPercent);
        Assert.Equal(0.0, s.WearPercent);
        Assert.Equal("excellent", s.Rating);
    }

    [Fact]
    public void WornBattery_ComputesHealthWearAndRating()
    {
        var s = Health(50000, 42500); // 85% health, 15% wear

        Assert.True(s.Available);
        Assert.Equal(85.0, s.HealthPercent);
        Assert.Equal(15.0, s.WearPercent);
        Assert.Equal("good", s.Rating);
    }

    [Theory]
    [InlineData(50000, 47500, "excellent")] // 95%
    [InlineData(50000, 45000, "excellent")] // 90% boundary
    [InlineData(50000, 40000, "good")]      // 80% boundary
    [InlineData(50000, 35000, "fair")]      // 70%
    [InlineData(50000, 30000, "fair")]      // 60% boundary
    [InlineData(50000, 25000, "poor")]      // 50%
    public void Rating_RespectsBoundaries(int designed, int full, string expected)
    {
        Assert.Equal(expected, Health(designed, full).Rating);
    }

    [Fact]
    public void FullExceedsDesign_ClampsToHundredPercentNoNegativeWear()
    {
        var s = Health(50000, 52000); // post-calibration overshoot

        Assert.Equal(100.0, s.HealthPercent);
        Assert.Equal(0.0, s.WearPercent);
        Assert.Equal("excellent", s.Rating);
    }

    [Fact]
    public void HealthPercent_RoundedToOneDecimal()
    {
        var s = Health(50000, 41234); // 82.468% -> 82.5

        Assert.Equal(82.5, s.HealthPercent);
        Assert.Equal(17.5, s.WearPercent);
    }

    [Fact]
    public void NullSnapshot_NotAvailableNoBattery()
    {
        var s = new BatteryHealthService(() => null).GetHealth();

        Assert.False(s.Available);
        Assert.Equal("unknown", s.Rating);
        Assert.Equal("no_battery", s.Message);
        Assert.Null(s.HealthPercent);
    }

    [Theory]
    [InlineData(null, 40000)]
    [InlineData(0, 40000)]
    [InlineData(-1, 40000)]
    [InlineData(50000, null)]
    public void MissingOrInvalidCapacity_NotAvailable(int? designed, int? full)
    {
        var s = Health(designed, full);

        Assert.False(s.Available);
        Assert.Equal("unknown", s.Rating);
        Assert.Equal("capacity_unreadable", s.Message);
    }

    [Fact]
    public void DeadBattery_FullIsZero_PoorZeroPercent()
    {
        var s = Health(50000, 0);

        Assert.True(s.Available);
        Assert.Equal(0.0, s.HealthPercent);
        Assert.Equal(100.0, s.WearPercent);
        Assert.Equal("poor", s.Rating);
    }

    [Fact]
    public void Compute_PassesThroughRawCapacities()
    {
        var s = BatteryHealthService.Compute(Cap(48000, 40000));

        Assert.Equal(48000, s.DesignedCapacityMwh);
        Assert.Equal(40000, s.FullChargedCapacityMwh);
    }
}
