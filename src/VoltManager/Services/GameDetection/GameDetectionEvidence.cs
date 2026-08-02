using System.Text.Json.Serialization;

namespace VoltManager.Services.GameDetection;

public sealed record GameDetectionEvidence(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("weight")] int Weight,
    [property: JsonPropertyName("description")] string Description);

public sealed record GameDetectionAssessment
{
    public static readonly GameDetectionAssessment Empty = new();

    [JsonPropertyName("primaryReason")] public string? PrimaryReason { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("level")] public string Level { get; init; } = "ignored";
    [JsonPropertyName("evidence")] public IReadOnlyList<GameDetectionEvidence> Evidence { get; init; } = Array.Empty<GameDetectionEvidence>();
}
