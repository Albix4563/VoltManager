using System.Text.Json.Serialization;

namespace VoltManager.Models;

public sealed class LanRemoteControlSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("port")] public int Port { get; set; } = 51737;
    [JsonPropertyName("allowPlanChange")] public bool AllowPlanChange { get; set; } = false;
    [JsonPropertyName("allowShutdown")] public bool AllowShutdown { get; set; } = false;
    [JsonPropertyName("allowRestart")] public bool AllowRestart { get; set; } = false;
}

public sealed record LanRemotePinVerifier
{
    [JsonPropertyName("salt")] public required string SaltBase64 { get; init; }
    [JsonPropertyName("hash")] public required string HashBase64 { get; init; }
    [JsonPropertyName("iterations")] public int Iterations { get; init; } = 600_000;
    [JsonPropertyName("digits")] public int Digits { get; init; }
}

public sealed record LanRemoteControlState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("running")] public bool Running { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("addresses")] public IReadOnlyList<string> Addresses { get; init; } = [];
    [JsonPropertyName("urls")] public IReadOnlyList<string> Urls { get; init; } = [];
    [JsonPropertyName("tlsFingerprintSha256")] public string? TlsFingerprintSha256 { get; init; }
    [JsonPropertyName("hasPin")] public bool HasPin { get; init; }
    [JsonPropertyName("allowPlanChange")] public bool AllowPlanChange { get; init; }
    [JsonPropertyName("allowShutdown")] public bool AllowShutdown { get; init; }
    [JsonPropertyName("allowRestart")] public bool AllowRestart { get; init; }
}
