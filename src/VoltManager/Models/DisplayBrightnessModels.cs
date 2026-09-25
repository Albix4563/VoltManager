using System.Text.Json.Serialization;

namespace VoltManager.Models;

public record DisplayBrightnessState
{
    [JsonPropertyName("supported")] public bool Supported { get; init; }
    [JsonPropertyName("percent")] public int? Percent { get; init; }
}
