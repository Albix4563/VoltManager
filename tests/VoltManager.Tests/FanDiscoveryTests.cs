using VoltManager.Fans;
using VoltManager.Models;
using Xunit;

namespace VoltManager.Tests;

public class FanDiscoveryTests
{
    [Fact]
    public void BuildTopology_classifies_known_fan_roles_without_inventing_control()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Hardware = "Nuvoton NCT6798D", Category = "motherboard", Name = "CPU Fan", Type = "fan", Value = 1240 },
                new() { Hardware = "Nuvoton NCT6798D", Category = "motherboard", Name = "SYS_FAN3", Type = "fan", Value = 840 },
                new() { Hardware = "Nuvoton NCT6798D", Category = "motherboard", Name = "AIO Pump", Type = "fan", Value = 2410 },
                new() { Hardware = "NVIDIA GeForce RTX", Category = "gpu", Name = "GPU Fan 1", Type = "fan", Value = 1580 },
                new() { Hardware = "AMD Ryzen", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 63.0 },
                new() { Hardware = "NVIDIA GeForce RTX", Category = "gpu", Name = "GPU Core", Type = "temp", Value = 67.0 },
            }
        };

        var topology = new FanDiscoveryService().BuildTopology(metrics);

        Assert.Equal(4, topology.Devices.Count);
        Assert.Equal(FanRole.CpuFan, topology.Devices.Single(x => x.SensorName == "CPU Fan").Role);
        Assert.Equal(FanRole.CaseFan, topology.Devices.Single(x => x.SensorName == "SYS_FAN3").Role);
        Assert.Equal(FanRole.Pump, topology.Devices.Single(x => x.SensorName == "AIO Pump").Role);
        Assert.Equal(FanRole.GpuFan, topology.Devices.Single(x => x.SensorName == "GPU Fan 1").Role);
        Assert.All(topology.Devices, fan =>
        {
            Assert.False(fan.Capabilities.ControlWritable);
            Assert.Equal(FanControlState.MonitorOnly, fan.ControlState);
        });
    }

    [Fact]
    public void BuildTopology_keeps_unknown_fans_unknown_and_assigns_unique_ids()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Hardware = "Generic SuperIO", Category = "motherboard", Name = "Fan #1", Type = "fan", Value = 900 },
                new() { Hardware = "Generic SuperIO", Category = "motherboard", Name = "Fan #1", Type = "fan", Value = 910 },
            }
        };

        var topology = new FanDiscoveryService().BuildTopology(metrics);

        Assert.Equal(2, topology.Devices.Count);
        Assert.All(topology.Devices, fan => Assert.Equal(FanRole.Unknown, fan.Role));
        Assert.Equal(2, topology.Devices.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BuildTopology_reports_lhm_control_capability_but_blocks_zero_minimum_without_fan_stop_semantics()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new()
                {
                    Identifier = "/lpc/nct6798d/fan/0",
                    Hardware = "Nuvoton NCT6798D",
                    Category = "motherboard",
                    Name = "CPU Fan",
                    Type = "fan",
                    Value = 1350,
                    ControlAvailable = true,
                    ControlIdentifier = "/lpc/nct6798d/control/0",
                    ControlMode = "Software",
                    ControlPercent = 52,
                    ControlMin = 0,
                    ControlMax = 100,
                },
                new() { Identifier = "/cpu/temp/0", Hardware = "CPU", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 61 },
            }
        };

        var fan = Assert.Single(new FanDiscoveryService().BuildTopology(metrics).Devices);

        Assert.True(fan.Capabilities.ControlReadable);
        Assert.True(fan.Capabilities.ControlWritable);
        Assert.True(fan.Capabilities.CanRestoreDefault);
        Assert.Equal(52, fan.Telemetry.ControlPercent);
        Assert.Equal(FanControlState.SafetyBlocked, fan.ControlState);
        Assert.Equal("/lpc/nct6798d/control/0", fan.ControlIdentifier);
        Assert.Contains("lpc/nct6798d/fan/0", fan.HardwareId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTopology_reacts_dynamically_when_isolated_control_transport_becomes_unavailable()
    {
        bool writesAllowed = true;
        var discovery = new FanDiscoveryService(() => writesAllowed);
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Identifier = "/fan/0", ControlIdentifier = "/control/0", Hardware = "Board", Category = "motherboard", Name = "CPU Fan", Type = "fan", Value = 1200, ControlAvailable = true, ControlMin = 25, ControlMax = 100 },
                new() { Identifier = "/cpu/temp/0", Hardware = "CPU", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 60 },
            }
        };

        Assert.True(Assert.Single(discovery.BuildTopology(metrics).Devices).Capabilities.ControlWritable);
        writesAllowed = false;
        FanDevice degraded = Assert.Single(discovery.BuildTopology(metrics).Devices);

        Assert.False(degraded.Capabilities.ControlWritable);
        Assert.Equal(FanControlState.MonitorOnly, degraded.ControlState);
    }

    [Fact]
    public void BuildTopology_degrades_control_to_read_only_when_isolated_hardware_service_is_unavailable()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Identifier = "/fan/0", ControlIdentifier = "/control/0", Hardware = "Board", Category = "motherboard", Name = "CPU Fan", Type = "fan", Value = 1200, ControlAvailable = true, ControlMin = 25, ControlMax = 100 },
                new() { Identifier = "/cpu/temp/0", Hardware = "CPU", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 60 },
            }
        };

        var fan = Assert.Single(new FanDiscoveryService(allowSoftwareControl: false).BuildTopology(metrics).Devices);

        Assert.False(fan.Capabilities.ControlWritable);
        Assert.False(fan.Capabilities.SoftwareCurveSupported);
        Assert.False(fan.Capabilities.CanRestoreDefault);
        Assert.Equal(FanControlState.MonitorOnly, fan.ControlState);
        Assert.Contains("readonly", fan.Capabilities.Backend, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTopology_marks_writable_fan_sensor_unavailable_when_thermal_telemetry_is_stale()
    {
        var metrics = new MetricsSnapshot
        {
            TimestampUtc = DateTime.UtcNow - TimeSpan.FromSeconds(20),
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Identifier = "/fan/0", ControlIdentifier = "/control/0", Hardware = "Board", Category = "motherboard", Name = "CPU Fan", Type = "fan", Value = 1200, ControlAvailable = true, ControlMin = 25, ControlMax = 100 },
                new() { Identifier = "/cpu/temp/0", Hardware = "CPU", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 60 },
            }
        };

        var fan = Assert.Single(new FanDiscoveryService().BuildTopology(metrics).Devices);

        Assert.Equal(FanControlState.SensorUnavailable, fan.ControlState);
        Assert.True(fan.Telemetry.IsStale);
        Assert.Contains("stale", fan.SafetyReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTopology_marks_writable_fan_sensor_unavailable_when_no_temperature_source_exists()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Identifier = "/fan/0", ControlIdentifier = "/control/0", Hardware = "Board", Category = "motherboard", Name = "SYS_FAN1", Type = "fan", Value = 900, ControlAvailable = true, ControlMin = 25, ControlMax = 100 },
            }
        };

        var fan = Assert.Single(new FanDiscoveryService().BuildTopology(metrics).Devices);

        Assert.Equal(FanControlState.SensorUnavailable, fan.ControlState);
        Assert.Contains("temperature", fan.SafetyReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTopology_associates_cpu_and_gpu_temperature_sources_by_role()
    {
        var metrics = new MetricsSnapshot
        {
            SensorsAvailable = true,
            Sensors = new List<SensorReading>
            {
                new() { Hardware = "Board", Category = "motherboard", Name = "CPU Fan", Type = "fan", Value = 1100 },
                new() { Hardware = "GPU A", Category = "gpu", Name = "GPU Fan", Type = "fan", Value = 1200 },
                new() { Hardware = "CPU A", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 55 },
                new() { Hardware = "CPU A", Category = "cpu", Name = "Core Max", Type = "temp", Value = 58 },
                new() { Hardware = "GPU A", Category = "gpu", Name = "GPU Core", Type = "temp", Value = 64 },
                new() { Hardware = "GPU A", Category = "gpu", Name = "GPU Hot Spot", Type = "temp", Value = 76 },
            }
        };

        var topology = new FanDiscoveryService().BuildTopology(metrics);
        var cpu = topology.Devices.Single(x => x.Role == FanRole.CpuFan);
        var gpu = topology.Devices.Single(x => x.Role == FanRole.GpuFan);

        Assert.All(cpu.AvailableTemperatureSensors, sensor => Assert.Equal("cpu", sensor.Category));
        Assert.All(gpu.AvailableTemperatureSensors, sensor => Assert.Equal("gpu", sensor.Category));
        Assert.Contains(gpu.AvailableTemperatureSensors, sensor => sensor.Name == "GPU Hot Spot");
    }
}
