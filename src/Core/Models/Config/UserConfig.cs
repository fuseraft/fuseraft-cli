using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

public sealed class UserConfig
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("apiKeyEnvVar")]
    public string ApiKeyEnvVar { get; set; } = string.Empty;

    [JsonPropertyName("skillCuration")]
    public SkillCurationConfig? SkillCuration { get; set; }

    // Never written to disk — populated at runtime from the OS keychain.
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    // Ollama runs locally without an API key, so a configured Ollama provider is
    // considered complete without one.
    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ModelId) &&
        (!string.IsNullOrWhiteSpace(ApiKey) || Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase));
}
