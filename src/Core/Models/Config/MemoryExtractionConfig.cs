using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the model used for the REPL's memory-extraction LLM call — the automatic
/// end-of-session save (see <c>ReplTurn.ExtractMemoriesOnExitAsync</c>) and the manual
/// <c>/memory save</c> command. See <c>fuseraft.Infrastructure.Memory.MemoryExtractor</c>.
/// </summary>
public record MemoryExtractionConfig
{
    /// <summary>
    /// Model alias or ID to use for extraction (e.g. a small/cheap model to keep the
    /// end-of-session extraction call inexpensive). Resolved by <c>ReplFactory.ResolveOverrideModel</c>:
    /// provider, endpoint, and API key are auto-detected from the ID prefix, or inherited from the
    /// REPL's main provider when the prefix isn't recognized, unless overridden by the fields below.
    /// Defaults to the REPL's main chat model when null or empty.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>Provider for <see cref="Model"/> (e.g. <c>openai</c>, <c>anthropic</c>). Null auto-detects it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    /// <summary>Base URL for <see cref="Model"/>, e.g. a gateway that serves it under a name the prefix table doesn't know.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }

    /// <summary>Env var holding the API key for <see cref="Model"/>. Null reuses the main provider's key when <see cref="Endpoint"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyEnvVar { get; init; }

    /// <summary>True when no field is set, so the whole section can be dropped from the config file.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Provider)
        && string.IsNullOrWhiteSpace(Endpoint) && string.IsNullOrWhiteSpace(ApiKeyEnvVar);
}
