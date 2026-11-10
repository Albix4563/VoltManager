using System.Text.Json;
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

    [Fact]
    public void SelectCpuCoreTemps_filters_non_core_and_invalid_readings()
    {
        var readings = new List<SensorReading>
        {
            new() { Category = "cpu", Type = "temp", Name = "CPU Package", Value = 75.0 },
            new() { Category = "cpu", Type = "temp", Name = "Core Max", Value = 74.0 },
            new() { Category = "cpu", Type = "temp", Name = "Core Average", Value = 65.0 },
            new() { Category = "cpu", Type = "temp", Name = "Core Avg", Value = 64.0 },
            new() { Category = "cpu", Type = "temp", Name = "Distance to TjMax", Value = 25.0 },
            new() { Category = "cpu", Type = "temp", Name = "TjMax", Value = 100.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU CCD #1", Value = 60.0 },
            new() { Category = "cpu", Type = "temp", Name = "CCD1", Value = 61.0 },
            new() { Category = "cpu", Type = "temp", Name = "Tctl/Tdie", Value = 68.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU Total", Value = 70.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU Socket", Value = 50.0 },
            new() { Category = "gpu", Type = "temp", Name = "GPU Core #1", Value = 55.0 },
            new() { Category = "cpu", Type = "clock", Name = "CPU Core #1", Value = 4200.0 },
            new() { Category = "cpu", Type = "temp", Name = "Core #1 - Core #4", Value = 60.0 },
            new() { Category = "cpu", Type = "temp", Name = "Core #1-4", Value = 60.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #1", Value = 0.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #2", Value = -5.0 },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #3", Value = double.NaN },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #4", Value = double.PositiveInfinity },
            // Valid readings
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #1", Value = 51.0, Identifier = "/intelcpu/0/temp/1" },
            new() { Category = "cpu", Type = "temp", Name = "Core #2", Value = 52.0, Identifier = "/intelcpu/0/temp/2" },
            new() { Category = "cpu", Type = "temp", Name = "P-Core #3", Value = 53.0, Identifier = "/intelcpu/0/temp/3" },
            new() { Category = "cpu", Type = "temp", Name = "E-Core #4", Value = 45.0, Identifier = "/intelcpu/0/temp/4" },
            new() { Category = "cpu", Type = "temp", Name = "Core #5/P-Core #5", Value = 54.0, Identifier = "/intelcpu/0/temp/5" },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #6 (P-Core 6)", Value = 55.0, Identifier = "/intelcpu/0/temp/6" },
        };

        var selected = SensorAggregation.SelectCpuCoreTemps(readings);

        Assert.Equal(6, selected.Count);
        Assert.Equal("CPU Core #1", selected[0].Name);
        Assert.Equal("Core #2", selected[1].Name);
        Assert.Equal("P-Core #3", selected[2].Name);
        Assert.Equal("Core #5/P-Core #5", selected[3].Name);
        Assert.Equal("CPU Core #6 (P-Core 6)", selected[4].Name);
        Assert.Equal("E-Core #4", selected[5].Name);
    }

    [Fact]
    public void SelectCpuCoreTemps_sorts_numerically_and_preserves_multi_cpu_stability()
    {
        var readings = new List<SensorReading>
        {
            // Shuffled numeric indices: 10, 2, 1, 11
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #10", Value = 58.0, Identifier = "/intelcpu/0/temp/10" },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #2", Value = 50.0, Identifier = "/intelcpu/0/temp/2" },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #1", Value = 48.0, Identifier = "/intelcpu/0/temp/1" },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #11", Value = 59.0, Identifier = "/intelcpu/0/temp/11" },
            // Duplicate reading with same identifier should be deduplicated
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #1 Stale", Value = 99.0, Identifier = "/intelcpu/0/temp/1" },
            // Second CPU in multi-socket setup
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #2", Value = 61.0, Identifier = "/intelcpu/1/temp/2", Hardware = "CPU 2" },
            new() { Category = "cpu", Type = "temp", Name = "CPU Core #1", Value = 60.0, Identifier = "/intelcpu/1/temp/1", Hardware = "CPU 2" },
        };

        var selected = SensorAggregation.SelectCpuCoreTemps(readings);

        Assert.Equal(6, selected.Count);
        // CPU 0 sorted numerically: 1, 2, 10, 11
        Assert.Equal("/intelcpu/0/temp/1", selected[0].Identifier);
        Assert.Equal(48.0, selected[0].Value);
        Assert.Equal("/intelcpu/0/temp/2", selected[1].Identifier);
        Assert.Equal("/intelcpu/0/temp/10", selected[2].Identifier);
        Assert.Equal("/intelcpu/0/temp/11", selected[3].Identifier);
        // CPU 1 kept distinct and sorted numerically: 1, 2
        Assert.Equal("/intelcpu/1/temp/1", selected[4].Identifier);
        Assert.Equal("/intelcpu/1/temp/2", selected[5].Identifier);
    }

    [Fact]
    public void SelectCpuCoreTemps_retains_more_than_32_cores_before_cap()
    {
        // 64 core processor (e.g. Threadripper/Epyc)
        var readings = Enumerable.Range(1, 64).Select(i => new SensorReading
        {
            Category = "cpu",
            Type = "temp",
            Name = $"CPU Core #{i}",
            Value = 40.0 + (i % 15),
            Identifier = $"/amdcpu/0/temp/{i}",
            Hardware = "AMD Ryzen Threadripper PRO",
        }).ToList();

        var coreTemps = SensorAggregation.SelectCpuCoreTemps(readings);
        var cappedForUi = SensorAggregation.CapForUi(readings);

        // All 64 cores retained in coreTemps
        Assert.Equal(64, coreTemps.Count);
        Assert.Equal("CPU Core #1", coreTemps[0].Name);
        Assert.Equal("CPU Core #64", coreTemps[63].Name);

        // General UI readings capped at MaxUiSensors (32)
        Assert.Equal(SensorAggregation.MaxUiSensors, cappedForUi.Count);
    }

    [Fact]
    public void MetricsPayload_cpuTemp_only_includes_headline_and_core_temps()
    {
        var cores = new List<SensorReading>
        {
            new() { Category = "cpu", Type = "temp", Name = "Core #1", Hardware = "Intel Core", Value = 52.0, Identifier = "/intelcpu/0/temp/1" },
            new() { Category = "cpu", Type = "temp", Name = "Core #2", Hardware = "Intel Core", Value = 55.0, Identifier = "/intelcpu/0/temp/2" },
        };
        var metrics = new MetricsSnapshot
        {
            Cpu = 45,
            Gpu = 60,
            CpuTemp = 55.0,
            GpuTemp = 62.0,
            CpuCoreTemps = cores,
        };

        object? payloadObj = WidgetWindow.MetricsPayload("cpuTemp", metrics);
        Assert.NotNull(payloadObj);

        string json = JsonSerializer.Serialize(payloadObj);
        using var doc = JsonDocument.Parse(json);

        // Must only contain cpuTemp and cpuCoreTemps
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
        Assert.Equal(55.0, doc.RootElement.GetProperty("cpuTemp").GetDouble());

        var coresProp = doc.RootElement.GetProperty("cpuCoreTemps");
        Assert.Equal(JsonValueKind.Array, coresProp.ValueKind);
        Assert.Equal(2, coresProp.GetArrayLength());
        Assert.Equal("Core #1", coresProp[0].GetProperty("name").GetString());
        Assert.Equal("Intel Core", coresProp[0].GetProperty("hardware").GetString());
        Assert.Equal("/intelcpu/0/temp/1", coresProp[0].GetProperty("identifier").GetString());
        Assert.Equal(52.0, coresProp[0].GetProperty("value").GetDouble());

        // Other widgets unchanged
        Assert.Null(WidgetWindow.MetricsPayload("clock", metrics));
        using var tempsDoc = JsonDocument.Parse(JsonSerializer.Serialize(WidgetWindow.MetricsPayload("temps", metrics)));
        Assert.Equal(2, tempsDoc.RootElement.EnumerateObject().Count());
        Assert.False(tempsDoc.RootElement.TryGetProperty("cpuCoreTemps", out _));
    }

    [Fact]
    public void Reduced_sampling_and_absent_data_handled_gracefully()
    {
        // 1. Absent data: empty list returned when no readings or empty readings
        Assert.Empty(SensorAggregation.SelectCpuCoreTemps(new List<SensorReading>()));
        Assert.Empty(SensorAggregation.SelectCpuCoreTemps(null!));

        // 2. MetricsSnapshot with absent data: null headline and empty list
        var absentMetrics = new MetricsSnapshot { CpuTemp = null, CpuCoreTemps = new() };
        string absentJson = JsonSerializer.Serialize(WidgetWindow.MetricsPayload("cpuTemp", absentMetrics));
        using var doc = JsonDocument.Parse(absentJson);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("cpuTemp").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("cpuCoreTemps").GetArrayLength());

        // 3. SensorReport with reduced sampling (details deactivated)
        var reducedReport = new SensorReport
        {
            CpuTemp = 50.0,
            DetailsAvailable = false,
            CpuCoreTemps = new(),
        };
        Assert.False(reducedReport.DetailsAvailable);
        Assert.Empty(reducedReport.CpuCoreTemps);
    }

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
