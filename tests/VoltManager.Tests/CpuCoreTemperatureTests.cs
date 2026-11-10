using System.Reflection;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;
using HardwareHost = VoltManager.HardwareService.HardwareHost;
using SensorReadingDto = VoltManager.HardwareService.SensorReadingDto;
using SensorReportDto = VoltManager.HardwareService.SensorReportDto;

namespace VoltManager.Tests;

public sealed class CpuCoreTemperatureTests
{
    private static SensorReading Core(string name, double value = 50, string id = "") => new()
        { Category = "cpu", Type = "temp", Name = name, Value = value, Identifier = id, Hardware = "CPU" };

    [Theory]
    [InlineData("CPU Package")]
    [InlineData("Core Max")]
    [InlineData("Core Average")]
    [InlineData("Core #1 Distance to TjMax")]
    [InlineData("CPU CCD #1")]
    [InlineData("Tctl/Tdie")]
    [InlineData("Core #1 - Core #4")]
    [InlineData("Core #1-4")]
    [InlineData("Core #1 Warning")]
    [InlineData("Core #99999999999999999999")]
    public void Non_core_names_are_excluded_by_both_producers(string name) =>
        AssertBoth([] , [Core(name)]);

    [Fact]
    public void Only_finite_positive_cpu_temperatures_are_selected()
    {
        var readings = new List<SensorReading>
        {
            Core("Core #1", 0), Core("Core #2", -1), Core("Core #3", double.NaN),
            Core("Core #4", double.PositiveInfinity),
            Core("Core #5") with { Category = "gpu" }, Core("Core #6") with { Type = "clock" },
            Core("CPU Core #1", 51, "/intelcpu/0/temperature/1"),
            Core("P-Core #2", 52, "/intelcpu/0/temperature/2"),
            Core("E-Core #3", 53, "/intelcpu/0/temperature/3"),
        };
        AssertBoth(["CPU Core #1", "P-Core #2", "E-Core #3"], readings);
    }

    [Fact]
    public void Numeric_order_identity_and_more_than_32_cores_are_preserved()
    {
        var readings = Enumerable.Range(1, 64).Reverse()
            .Select(index => Core($"Core #{index}", id: $"/intelcpu/0/temperature/{index}")).ToList();
        readings.Add(Core("Core #1 duplicate", id: "/intelcpu/0/temperature/1"));
        readings.Add(Core("Core #1 CPU 2", id: "/intelcpu/1/temperature/1"));
        var expected = Enumerable.Range(1, 64).Select(index => $"Core #{index}").Append("Core #1 CPU 2").ToArray();
        AssertBoth(expected, readings);
    }

    [Fact]
    public void Service_json_is_deserialized_without_losing_core_temperatures()
    {
        var cores = HardwareHost.SelectCpuCoreTemps([new SensorReadingDto
        {
            Category = "cpu", Type = "temp", Name = "Core #1", Value = 52.5,
            Identifier = "/intelcpu/0/temperature/1", Hardware = "CPU",
        }]);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var report = JsonSerializer.Deserialize<SensorReport>(JsonSerializer.Serialize(
            new SensorReportDto { CpuTemp = 55, CpuCoreTemps = cores, DetailsAvailable = true }, options), options)!;
        Assert.Equal(55, report.CpuTemp);
        Assert.Equal(52.5, Assert.Single(report.CpuCoreTemps).Value);
        Assert.Equal("/intelcpu/0/temperature/1", report.CpuCoreTemps[0].Identifier);
    }

    [Fact]
    public void Monitor_tick_propagates_core_temperatures_and_clears_them_when_visual_sampling_stops()
    {
        var core = Core("Core #1", 52.5, "/intelcpu/0/temperature/1");
        var access = new StubAccess { Report = new SensorReport { CpuTemp = 55, CpuCoreTemps = [core], DetailsAvailable = true } };
        using var monitor = new MonitorService(access);
        var tick = typeof(MonitorService).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        monitor.SetSamplingDemand(new(true, false, false));
        tick.Invoke(monitor, null);
        Assert.Equal(core, Assert.Single(monitor.Latest.CpuCoreTemps));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(WidgetWindow.MetricsPayload("cpuTemp", monitor.Latest)));
        Assert.Equal(52.5, payload.RootElement.GetProperty("cpuCoreTemps")[0].GetProperty("value").GetDouble());
        monitor.SetSamplingDemand(new(false, true, false));
        tick.Invoke(monitor, null);
        Assert.Empty(monitor.Latest.CpuCoreTemps);
        Assert.Equal(55, monitor.Latest.CpuTemp);
        access.Report = SensorReport.Empty;
        monitor.SetSamplingDemand(new(true, false, false));
        tick.Invoke(monitor, null);
        Assert.Empty(monitor.Latest.CpuCoreTemps);
    }

    private static void AssertBoth(string[] names, List<SensorReading> readings)
    {
        Assert.Equal(names, SensorAggregation.SelectCpuCoreTemps(readings).Select(sensor => sensor.Name));
        var dto = readings.Select(sensor => new SensorReadingDto
        {
            Category = sensor.Category, Type = sensor.Type, Name = sensor.Name, Value = sensor.Value,
            Identifier = sensor.Identifier ?? "", Hardware = sensor.Hardware,
        }).ToList();
        Assert.Equal(names, HardwareHost.SelectCpuCoreTemps(dto).Select(sensor => sensor.Name));
    }

    private sealed class StubAccess : IHardwareAccess
    {
        public SensorReport Report { get; set; } = SensorReport.Empty;
        public bool Available => true;
        public SensorReport Read(bool force = false) => Report;
        public SensorReport Read(HardwareSampleRequest request, bool force = false) => Report;
        public void Invalidate() { }
        public void Dispose() { }
    }
}
