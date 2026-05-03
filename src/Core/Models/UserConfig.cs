using System.Text.Json.Serialization;

namespace fuseraft.Core.Models;

public sealed class UserConfig
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    // Never written to disk — populated at runtime from the OS keychain.
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ModelId) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
