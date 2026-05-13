using System.Text.Json.Serialization;

namespace HookVault.Configuration;

public sealed class ProviderConfig
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("forwardUrl")]
    public string ForwardUrl { get; init; } = string.Empty;

    [JsonPropertyName("validation")]
    public ValidationConfig? Validation { get; init; }
}
