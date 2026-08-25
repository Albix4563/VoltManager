using System.Text.Json.Serialization;

namespace VoltManager.Performance;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceProfile
{
    Full,
    Balanced,
    Gaming,
    Critical,
}

/// <summary>
/// Operational resource state for elastic VoltManager work. Safety-critical sampling,
/// thermal automation does not depend on this profile.
/// </summary>
public sealed record ResourcePressureState
{
    public ResourceProfile Profile { get; init; } = ResourceProfile.Full;
    public bool GameActive { get; init; }
    public bool UiVisible { get; init; } = true;
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double RamPercent { get; init; }
    public string Reason { get; init; } = "normal";
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
}
