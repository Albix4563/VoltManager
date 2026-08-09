using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanSafetyPolicyTests
{
    private static FanDevice Fan(double min = 25, double max = 100, FanRole role = FanRole.CpuFan) => new()
    {
        Id = "fan-1",
        ControlIdentifier = "/gpu-nvidia/0/control/0",
        Role = role,
        ControlState = FanControlState.ControlAvailable,
        Capabilities = new FanCapabilities
        {
            RpmReadable = true,
            ControlReadable = true,
            ControlWritable = true,
            FixedControlSupported = true,
            SoftwareCurveSupported = true,
            CanRestoreDefault = true,
            MinimumControl = min,
            MaximumControl = max,
            Backend = "test",
        },
        AvailableTemperatureSensors = new List<FanTemperatureSensor>
        {
            new() { Id = "temp-1", Category = "cpu", Name = "CPU Package", Value = 60 },
        },
    };

    [Fact]
    public void Manual_control_inside_verified_range_is_allowed()
    {
        var policy = new FanSafetyPolicy();
        var decision = policy.Validate(Fan(), new FanConfiguration
        {
            Mode = FanMode.Manual,
            FixedControlPercent = 55,
        }, referenceTemperature: 60);

        Assert.True(decision.Allowed);
        Assert.Equal(55, decision.EffectiveControlPercent);
    }

    [Fact]
    public void Zero_minimum_without_explicit_fan_stop_is_blocked()
    {
        var decision = new FanSafetyPolicy().Validate(Fan(min: 0), new FanConfiguration
        {
            Mode = FanMode.Manual,
            FixedControlPercent = 40,
        }, referenceTemperature: 50);

        Assert.False(decision.Allowed);
        Assert.Equal("minimum_not_verified", decision.Code);
    }

    [Fact]
    public void Pump_software_control_is_blocked_without_dedicated_backend_semantics()
    {
        var decision = new FanSafetyPolicy().Validate(Fan(role: FanRole.Pump), new FanConfiguration
        {
            Mode = FanMode.Manual,
            FixedControlPercent = 70,
        }, referenceTemperature: 50);

        Assert.False(decision.Allowed);
        Assert.Equal("pump_safety_block", decision.Code);
    }

    [Fact]
    public void Blocking_external_software_prevents_control()
    {
        var decision = new FanSafetyPolicy().Validate(Fan(), new FanConfiguration
        {
            Mode = FanMode.Manual,
            FixedControlPercent = 55,
        }, new[]
        {
            new FanExternalSoftwareNotice { SoftwareName = "Other", BlocksControl = true },
        }, 60);

        Assert.False(decision.Allowed);
        Assert.Equal("external_controller", decision.Code);
    }

    [Fact]
    public void High_temperature_raises_output_to_safety_floor()
    {
        var decision = new FanSafetyPolicy().Validate(Fan(), new FanConfiguration
        {
            Mode = FanMode.Manual,
            FixedControlPercent = 30,
        }, referenceTemperature: 85);

        Assert.True(decision.Allowed);
        Assert.True(decision.SafetyOverrideActive);
        Assert.True(decision.EffectiveControlPercent >= 85);
    }

    [Fact]
    public void Curve_requires_monotonic_points_and_selected_sensor()
    {
        var decision = new FanSafetyPolicy().Validate(Fan(), new FanConfiguration
        {
            Mode = FanMode.Curve,
            SensorId = "temp-1",
            Curve = new List<FanCurvePoint>
            {
                new() { Temperature = 40, ControlPercent = 60 },
                new() { Temperature = 70, ControlPercent = 40 },
            },
        }, referenceTemperature: 60);

        Assert.False(decision.Allowed);
        Assert.Equal("curve_not_monotonic", decision.Code);
    }
}
