using System.Text.Json.Serialization;

namespace HookVault.Configuration;

public sealed class ValidationConfig
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = "hmac-sha256";

    [JsonPropertyName("secretEnvVar")]
    public string SecretEnvVar { get; init; } = string.Empty;

    [JsonPropertyName("signatureHeader")]
    public string SignatureHeader { get; init; } = string.Empty;

    [JsonPropertyName("payloadFormat")]
    public string PayloadFormat { get; init; } = "{body}";

    [JsonPropertyName("signaturePattern")]
    public string? SignaturePattern { get; init; }

    [JsonPropertyName("timestampPattern")]
    public string? TimestampPattern { get; init; }
}
