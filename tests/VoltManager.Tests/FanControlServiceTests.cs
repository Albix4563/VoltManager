using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanControlServiceTests
{
    [Fact]
    public void Preview_never_writes_to_backend()
    {
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(), backend);

        FanConfigurationPreview preview = service.Preview("fan-1", Manual(55));

        Assert.True(preview.Valid);
        Assert.Equal(55, preview.EffectiveControlPercent);
        Assert.Equal(0, backend.SoftwareWrites);
        Assert.Equal(0, backend.Restores);
    }

    [Fact]
    public void Apply_writes_only_after_revision_and_safety_validation()
    {
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(), backend);

        FanApplyResult result = service.Apply("rev-1", "fan-1", Manual(55));

        Assert.True(result.Success);
        Assert.Equal(1, backend.SoftwareWrites);
        Assert.Equal(55, backend.LastPercent);
        Assert.Single(service.Current.Sessions);
    }

    [Fact]
    public void Stale_topology_revision_is_rejected_without_write()
    {
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(), backend);

        FanApplyResult result = service.Apply("old-revision", "fan-1", Manual(55));

        Assert.False(result.Success);
        Assert.Equal("topology_changed", result.Code);
        Assert.Equal(0, backend.SoftwareWrites);
    }

    [Fact]
    public void Restore_releases_active_session_to_backend_default()
    {
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(), backend);
        Assert.True(service.Apply("rev-1", "fan-1", Manual(55)).Success);

        FanApplyResult restored = service.Restore("fan-1");

        Assert.True(restored.Success);
        Assert.Equal(1, backend.Restores);
        Assert.Empty(service.Current.Sessions);
    }

    [Fact]
    public void Restore_does_not_touch_controller_when_external_utility_is_active_and_no_session_is_owned()
    {
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(blocked: true), backend);

        FanApplyResult result = service.Restore("fan-1");

        Assert.False(result.Success);
        Assert.Equal("external_controller", result.Code);
        Assert.Equal(0, backend.Restores);
    }

    [Fact]
    public void Watchdog_restores_default_when_external_controller_appears()
    {
        bool blocked = false;
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(blocked), backend);
        Assert.True(service.Apply("rev-1", "fan-1", Manual(55)).Success);

        blocked = true;
        service.RunWatchdogOnceForTests();

        Assert.Equal(1, backend.Restores);
        Assert.Empty(service.Current.Sessions);
        Assert.Equal("external_controller_detected", service.Current.LastError);
    }

    [Fact]
    public void Watchdog_restores_default_when_temperature_reading_becomes_stale()
    {
        bool stale = false;
        var backend = new FakeBackend();
        using var service = new FanControlService(_ => Topology(false, stale), backend);
        Assert.True(service.Apply("rev-1", "fan-1", Manual(55)).Success);

        stale = true;
        service.RunWatchdogOnceForTests();

        Assert.Equal(1, backend.Restores);
        Assert.Empty(service.Current.Sessions);
        Assert.Equal("sensor_unavailable", service.Current.LastError);
    }

    private static FanConfiguration Manual(double value) => new()
    {
        Mode = FanMode.Manual,
        SensorId = "temp-1",
        FixedControlPercent = value,
    };

    private static FanTopology Topology(bool blocked = false, bool stale = false) => new()
    {
        Revision = "rev-1",
        SensorsAvailable = true,
        ExternalSoftware = blocked
            ? new List<FanExternalSoftwareNotice>
            {
                new() { SoftwareName = "External", BlocksControl = true, Confidence = FanConflictConfidence.Possible },
            }
            : new List<FanExternalSoftwareNotice>(),
        Devices = new List<FanDevice>
        {
            new()
            {
                Id = "fan-1",
                HardwareId = "/fan/1",
                ControlIdentifier = "/control/1",
                HardwareName = "Board",
                SensorName = "CPU Fan",
                DisplayName = "CPU Fan",
                Role = FanRole.CpuFan,
                ControlState = blocked ? FanControlState.ExternalControllerDetected : stale ? FanControlState.SensorUnavailable : FanControlState.ControlAvailable,
                Capabilities = new FanCapabilities
                {
                    RpmReadable = true,
                    ControlWritable = true,
                    FixedControlSupported = true,
                    SoftwareCurveSupported = true,
                    CanRestoreDefault = true,
                    MinimumControl = 25,
                    MaximumControl = 100,
                    Backend = "fake",
                },
                Telemetry = new FanTelemetry
                {
                    Rpm = 1200,
                    ReferenceTemperature = 60,
                    LastUpdatedUtc = stale ? DateTime.UtcNow - TimeSpan.FromSeconds(30) : DateTime.UtcNow,
                    IsStale = stale,
                },
                AvailableTemperatureSensors = new List<FanTemperatureSensor>
                {
                    new() { Id = "temp-1", Category = "cpu", Hardware = "CPU", Name = "CPU Package", Value = 60 },
                },
            }
        },
    };

    private sealed class FakeBackend : IFanBackend
    {
        public string Name => "fake";
        public int SoftwareWrites { get; private set; }
        public int Restores { get; private set; }
        public double? LastPercent { get; private set; }

        public bool CanHandle(FanDevice fan) => fan.Capabilities.Backend == Name;

        public FanBackendWriteResult SetSoftware(FanDevice fan, double percent)
        {
            SoftwareWrites++;
            LastPercent = percent;
            return new FanBackendWriteResult
            {
                Success = true,
                Code = "ok",
                EffectiveControlPercent = percent,
                Mode = "Software",
            };
        }

        public FanBackendWriteResult RestoreDefault(FanDevice fan)
        {
            Restores++;
            return new FanBackendWriteResult { Success = true, Code = "ok", Mode = "Default" };
        }
    }
}
