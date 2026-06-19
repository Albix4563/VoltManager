using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class PowerFlowServiceTests
{
    private static BatteryPowerSnapshot Snap(
        bool powerOnline = false,
        bool charging = false,
        bool discharging = false,
        int? chargeRateMw = null,
        int? dischargeRateMw = null,
        int? remainingMwh = null,
        int? fullMwh = null,
        int? voltageMv = null)
        => new()
        {
            PowerOnline = powerOnline,
            Charging = charging,
            Discharging = discharging,
            ChargeRateMw = chargeRateMw,
            DischargeRateMw = dischargeRateMw,
            RemainingCapacityMwh = remainingMwh,
            FullChargedCapacityMwh = fullMwh,
            VoltageMv = voltageMv,
        };

    private static BatteryPowerState State(BatteryPowerSnapshot? s)
        => new PowerFlowService(() => s).GetState();

    [Fact]
    public void NullSnapshot_NotAvailableNoBattery()
    {
        var s = State(null);

        Assert.False(s.Available);
        Assert.Equal("unknown", s.Status);
        Assert.Equal("none", s.TimeKind);
        Assert.Equal("no_battery", s.Message);
        Assert.Null(s.PowerWatts);
    }

    [Fact]
    public void Discharging_NegativeWatts_TimeToEmpty()
    {
        // 15 W draw, 30 Wh remaining -> 2h to empty.
        var s = State(Snap(discharging: true, dischargeRateMw: 15000, remainingMwh: 30000, fullMwh: 50000));

        Assert.True(s.Available);
        Assert.Equal("discharging", s.Status);
        Assert.Equal(-15.0, s.PowerWatts);
        Assert.Equal("toEmpty", s.TimeKind);
        Assert.Equal(120, s.MinutesRemaining);
        Assert.Equal(60, s.BatteryPercent);
    }

    [Fact]
    public void Charging_PositiveWatts_TimeToFull()
    {
        // 20 W in, need 30 Wh to fill -> 90 min to full.
        var s = State(Snap(powerOnline: true, charging: true, chargeRateMw: 20000, remainingMwh: 20000, fullMwh: 50000));

        Assert.True(s.Available);
        Assert.Equal("charging", s.Status);
        Assert.Equal(20.0, s.PowerWatts);
        Assert.True(s.OnAc);
        Assert.Equal("toFull", s.TimeKind);
        Assert.Equal(90, s.MinutesRemaining);
    }

    [Fact]
    public void OnAcAtFullCharge_StatusFull_ZeroWatts()
    {
        var s = State(Snap(powerOnline: true, remainingMwh: 50000, fullMwh: 50000));

        Assert.True(s.Available);
        Assert.Equal("full", s.Status);
        Assert.Equal(0.0, s.PowerWatts);
        Assert.Equal("none", s.TimeKind);
        Assert.Equal(100, s.BatteryPercent);
    }

    [Fact]
    public void OnAcNotFull_NoCurrent_StatusIdle()
    {
        var s = State(Snap(powerOnline: true, remainingMwh: 40000, fullMwh: 50000));

        Assert.Equal("idle", s.Status);
        Assert.Equal(0.0, s.PowerWatts);
        Assert.Equal("none", s.TimeKind);
    }

    [Fact]
    public void NegativeFirmwareRate_UsesMagnitude()
    {
        // Some firmware reports discharge as a negative sint32.
        var s = State(Snap(discharging: true, dischargeRateMw: -15000, remainingMwh: 30000, fullMwh: 50000));

        Assert.Equal(-15.0, s.PowerWatts);
        Assert.Equal(120, s.MinutesRemaining);
    }

    [Fact]
    public void DischargingButZeroRate_FallsBackToIdle_NoEstimate()
    {
        var s = State(Snap(discharging: true, dischargeRateMw: 0, remainingMwh: 30000, fullMwh: 50000));

        Assert.Equal("idle", s.Status);
        Assert.Equal(0.0, s.PowerWatts);
        Assert.Equal("none", s.TimeKind);
        Assert.Null(s.MinutesRemaining);
    }

    [Fact]
    public void Percent_ClampsAboveHundred()
    {
        // Post-calibration overshoot: remaining > full.
        var s = State(Snap(powerOnline: true, remainingMwh: 52000, fullMwh: 50000));

        Assert.Equal(100, s.BatteryPercent);
        Assert.Equal("full", s.Status);
    }

    [Fact]
    public void Voltage_ConvertedMvToVolts()
    {
        var s = State(Snap(discharging: true, dischargeRateMw: 10000, remainingMwh: 20000, fullMwh: 40000, voltageMv: 11400));

        Assert.Equal(11.4, s.VoltageVolts);
    }

    [Fact]
    public void MissingFullCapacity_PercentNull_ChargingHasNoTimeEstimate()
    {
        var s = State(Snap(powerOnline: true, charging: true, chargeRateMw: 18000, remainingMwh: 20000, fullMwh: null));

        Assert.Null(s.BatteryPercent);
        Assert.Equal("charging", s.Status);
        Assert.Equal(18.0, s.PowerWatts);
        Assert.Equal("none", s.TimeKind);
        Assert.Null(s.MinutesRemaining);
    }

    [Fact]
    public void OnBatteryIdle_ZeroWatts_NotFull()
    {
        var s = State(Snap(powerOnline: false, remainingMwh: 30000, fullMwh: 50000));

        Assert.Equal("idle", s.Status);
        Assert.Equal(0.0, s.PowerWatts);
        Assert.False(s.OnAc);
    }

    [Fact]
    public void Compute_PassesThroughRawCapacities()
    {
        var s = PowerFlowService.Compute(Snap(discharging: true, dischargeRateMw: 12000, remainingMwh: 24000, fullMwh: 48000));

        Assert.Equal(24000, s.RemainingCapacityMwh);
        Assert.Equal(48000, s.FullChargedCapacityMwh);
    }
}
