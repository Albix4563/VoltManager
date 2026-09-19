using System.Text.Json.Serialization;

namespace VoltManager.Models;

public record MetricsSnapshot
{
    [JsonPropertyName("timestampUtc")] public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("cpu")] public double Cpu { get; init; }
    [JsonPropertyName("gpu")] public double Gpu { get; init; }
    [JsonPropertyName("gpuAvailable")] public bool GpuAvailable { get; init; }
    [JsonPropertyName("gpuSampledAtUtc")] public DateTime? GpuSampledAtUtc { get; init; }
    [JsonPropertyName("ramPct")] public double RamPct { get; init; }
    [JsonPropertyName("ramUsedGb")] public double RamUsedGb { get; init; }
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("disk")] public double Disk { get; init; }
    [JsonPropertyName("diskAvailable")] public bool DiskAvailable { get; init; }
    [JsonPropertyName("diskSampledAtUtc")] public DateTime? DiskSampledAtUtc { get; init; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public double? GpuTemp { get; init; }
    [JsonPropertyName("cpuClock")] public double? CpuClock { get; init; }
    [JsonPropertyName("ramClock")] public double? RamClock { get; init; }
    [JsonPropertyName("sensorsAvailable")] public bool SensorsAvailable { get; init; }
    [JsonPropertyName("sensorSampledAtUtc")] public DateTime? SensorSampledAtUtc { get; init; }
    [JsonPropertyName("sensorDetailsAvailable")] public bool SensorDetailsAvailable { get; init; }
    [JsonPropertyName("sensors")] public List<SensorReading> Sensors { get; init; } = new();
    [JsonPropertyName("vram")] public VramMemorySnapshot? Vram { get; init; }
}

public record VramMemorySnapshot
{
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("pressurePercent")] public double? PressurePercent { get; init; }
    [JsonPropertyName("timestampUtc")] public DateTime? TimestampUtc { get; init; }
    [JsonPropertyName("adapters")] public List<VramAdapterMemorySample> Adapters { get; init; } = new();
}

public record VramAdapterMemorySample
{
    [JsonPropertyName("adapter")] public string Adapter { get; init; } = "";
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
    [JsonPropertyName("capacityBytes")] public long CapacityBytes { get; init; }
    [JsonPropertyName("pressurePercent")] public double PressurePercent { get; init; }
    [JsonPropertyName("timestampUtc")] public DateTime TimestampUtc { get; init; }
}

public record SensorReading
{
    [JsonPropertyName("identifier")] public string? Identifier { get; init; }
    [JsonPropertyName("hardware")] public string Hardware { get; init; } = "";  // device name
    [JsonPropertyName("category")] public string Category { get; init; } = "";  // cpu|gpu|storage|motherboard
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";          // temp|clock
    [JsonPropertyName("value")] public double Value { get; init; }
}

public record SystemInfo
{
    [JsonPropertyName("cpuName")] public string CpuName { get; init; } = "";
    [JsonPropertyName("gpuName")] public string GpuName { get; init; } = "";
    [JsonPropertyName("ramTotalGb")] public double RamTotalGb { get; init; }
    [JsonPropertyName("osVersion")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("hasBattery")] public bool HasBattery { get; init; }
    [JsonPropertyName("logicalCores")] public int LogicalCores { get; init; }
}

public record ProcessInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("cpuPercent")] public double CpuPercent { get; init; }
    [JsonPropertyName("ramMb")] public double RamMb { get; init; }
    [JsonPropertyName("instances")] public int Instances { get; init; } = 1;
}

/// <summary>
/// Snapshot dettagliato della memoria RAM (in GB e percentuale).
/// </summary>
public record MemoryStatus
{
    [JsonPropertyName("totalGb")]     public double TotalGb     { get; init; }
    [JsonPropertyName("inUseGb")]     public double InUseGb     { get; init; }
    [JsonPropertyName("standbyGb")]   public double StandbyGb   { get; init; }
    [JsonPropertyName("freeGb")]      public double FreeGb      { get; init; }
    [JsonPropertyName("standbyPct")]  public double StandbyPct  { get; init; }
    [JsonPropertyName("inUsePct")]    public double InUsePct    { get; init; }
}
