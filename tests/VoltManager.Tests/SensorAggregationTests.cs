using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class SensorAggregationTests
{
    private static SensorReading Temp(string category, string name, double value) =>
        new() { Category = category, Name = name, Type = "temp", Value = value, Hardware = "hw" };

    [Theory]
    [InlineData(HardwareType.Cpu, "cpu")]
    [InlineData(HardwareType.GpuNvidia, "gpu")]
    [InlineData(HardwareType.GpuAmd, "gpu")]
    [InlineData(HardwareType.GpuIntel, "gpu")]
    [InlineData(HardwareType.Storage, "storage")]
    [InlineData(HardwareType.Motherboard, "motherboard")]
    [InlineData(HardwareType.SuperIO, "motherboard")]
    public void MapCategory_MapsHardwareTypes(HardwareType type, string expected)
    {
        Assert.Equal(expected, SensorAggregation.MapCategory(type));
    }

    [Fact]
    public void SelectCpuTemp_PrefersAmdTctlTdie()
    {
        var readings = new List<SensorReading>
        {
            Temp("cpu", "Core #1", 80),
            Temp("cpu", "Core (Tctl/Tdie)", 62.5),
        };
        Assert.Equal(62.5, SensorAggregation.SelectCpuTemp(readings));
    }

    [Fact]
    public void SelectCpuTemp_PrefersIntelPackage()
    {
        var readings = new List<SensorReading>
        {
            Temp("cpu", "Core #1", 80),
            Temp("cpu", "CPU Package", 55),
        };
        Assert.Equal(55, SensorAggregation.SelectCpuTemp(readings));
    }

    [Fact]
    public void SelectCpuTemp_FallsBackToMax()
    {
        var readings = new List<SensorReading>
        {
            Temp("cpu", "Core #1", 48),
            Temp("cpu", "Core #2", 51),
        };
        Assert.Equal(51, SensorAggregation.SelectCpuTemp(readings));
    }

    [Fact]
    public void SelectCpuTemp_NullWhenNoCpuTemps()
    {
        var readings = new List<SensorReading>
        {
            Temp("gpu", "GPU Core", 60),
            new() { Category = "cpu", Name = "CPU Fan", Type = "fan", Value = 1200, Hardware = "hw" },
        };
        Assert.Null(SensorAggregation.SelectCpuTemp(readings));
    }

    [Fact]
    public void SelectGpuTemp_PrefersGpuCore()
    {
        var readings = new List<SensorReading>
        {
            Temp("gpu", "GPU Hot Spot", 75),
            Temp("gpu", "GPU Core", 61),
        };
        Assert.Equal(61, SensorAggregation.SelectGpuTemp(readings));
    }

    [Fact]
    public void SelectGpuTemp_FallsBackToFirst()
    {
        var readings = new List<SensorReading> { Temp("gpu", "GPU Memory", 70) };
        Assert.Equal(70, SensorAggregation.SelectGpuTemp(readings));
    }

    [Fact]
    public void MetricsSnapshot_SerializesSensorsCamelCase()
    {
        var snapshot = new MetricsSnapshot
        {
            CpuTemp = 55.5,
            GpuTemp = null,
            SensorsAvailable = true,
            Sensors = new List<SensorReading> { Temp("cpu", "CPU Package", 55.5) },
        };
        string json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"cpuTemp\":55.5", json);
        Assert.Contains("\"gpuTemp\":null", json);
        Assert.Contains("\"sensorsAvailable\":true", json);
        Assert.Contains("\"category\":\"cpu\"", json);

        var back = JsonSerializer.Deserialize<MetricsSnapshot>(json)!;
        Assert.Equal(55.5, back.CpuTemp);
        Assert.Single(back.Sensors);
        Assert.Equal("CPU Package", back.Sensors[0].Name);
    }
}
