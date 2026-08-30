using VoltManager.Models;
using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public class HardwareServiceClientTests
{
    [Fact]
    public void Client_bootstraps_named_pipe_service_for_isolated_monitoring()
    {
        using HardwareServiceClient? client = HardwareServiceClient.TryStart();
        Assert.NotNull(client);
    }

    [Fact]
    public void Deferred_access_is_non_blocking_and_forwards_when_ready()
    {
        using var release = new ManualResetEventSlim();
        var inner = new StubHardwareAccess();
        using var access = new DeferredHardwareAccess(() =>
        {
            release.Wait();
            return inner;
        });

        Assert.False(access.Available);
        Assert.Same(SensorReport.Empty, access.Read());

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => access.Available, TimeSpan.FromSeconds(2)));
        Assert.Same(inner.Report, access.Read());
    }

    [Fact]
    public void Client_reuses_two_second_sensor_sample_unless_forced_or_invalidated()
    {
        using HardwareServiceClient? client = HardwareServiceClient.TryStart();
        Assert.NotNull(client);

        long before = client.RequestCount;
        client.Read();
        long afterFirstRead = client.RequestCount;
        client.Read();

        Assert.True(afterFirstRead > before);
        Assert.Equal(afterFirstRead, client.RequestCount);

        client.Read(force: true);
        Assert.True(client.RequestCount > afterFirstRead);

        client.Invalidate();
        long afterInvalidate = client.RequestCount;
        client.Read();
        Assert.True(client.RequestCount > afterInvalidate);
    }

    [Fact]
    public void Sensor_payload_prefers_temperatures_and_is_capped()
    {
        var readings = Enumerable.Range(0, 40)
            .Select(i => new SensorReading { Name = "Clock " + i, Type = "clock", Value = i + 1 })
            .ToList();
        readings.InsertRange(0, Enumerable.Range(0, 8)
            .Select(i => new SensorReading { Name = "Temp " + i, Type = "temp", Value = 40 + i }));

        var capped = SensorAggregation.CapForUi(readings);

        Assert.Equal(SensorAggregation.MaxUiSensors, capped.Count);
        Assert.All(capped.Take(8), reading => Assert.Equal("temp", reading.Type));
        Assert.Equal(24, capped.Count(reading => reading.Type == "clock"));
    }

    private sealed class StubHardwareAccess : IHardwareAccess
    {
        public SensorReport Report { get; } = new();
        public bool Available => true;
        public SensorReport Read(bool force = false) => Report;
        public void Invalidate() { }
        public void Dispose() { }
    }
}
