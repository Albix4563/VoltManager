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
    public void Client_cache_requires_request_coverage_and_respects_requested_interval()
    {
        DateTime t0 = DateTime.UtcNow;
        var temperaturesOnly = new HardwareSampleRequest(true, false, TimeSpan.FromSeconds(2));
        var full = new HardwareSampleRequest(true, true, TimeSpan.FromSeconds(10));

        Assert.True(HardwareServiceClient.IsReadFresh(t0, t0.AddSeconds(1), temperaturesOnly, temperaturesOnly));
        Assert.False(HardwareServiceClient.IsReadFresh(t0, t0.AddSeconds(1), temperaturesOnly, full));
        Assert.True(HardwareServiceClient.IsReadFresh(t0, t0.AddSeconds(9), full, full));
        Assert.False(HardwareServiceClient.IsReadFresh(t0, t0.AddSeconds(10), full, full));
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

    [Theory]
    [InlineData("4", "5", true)]
    [InlineData("5", "5", false)]
    [InlineData("6", "5", false)]
    [InlineData(null, "5", false)]
    [InlineData("abc", "5", false)]
    public void Late_replies_from_timed_out_calls_are_recognized_as_stale(string? responseId, string requestId, bool stale)
        => Assert.Equal(stale, HardwareServiceClient.IsStaleResponseId(responseId, requestId));

    private sealed class StubHardwareAccess : IHardwareAccess
    {
        public SensorReport Report { get; } = new();
        public bool Available => true;
        public SensorReport Read(bool force = false) => Report;
        public SensorReport Read(HardwareSampleRequest request, bool force = false) => Report;
        public void Invalidate() { }
        public void Dispose() { }
    }
}
