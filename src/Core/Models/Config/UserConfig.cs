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

    /// <summary>Persisted default sampling parameters for new REPL sessions.</summary>
    [JsonPropertyName("sampling")]
    public SamplingDefaultsConfig Sampling { get; set; } = new();

    /// <summary>Persisted REPL startup defaults (banner, verbosity, plugins, safe mode, context budget).</summary>
    [JsonPropertyName("repl")]
    public ReplDefaultsConfig Repl { get; set; } = new();

    /// <summary>MCP servers added via the REPL's <c>/mcp add</c> wizard.</summary>
    [JsonPropertyName("mcpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// Global default OpenTelemetry export settings, used by project orchestration configs
    /// that don't declare their own <c>Telemetry</c> section (see
    /// <c>OrchestratorConfigLoader.ApplyGlobalDefaults</c>). Null means telemetry is disabled
    /// by default.
    /// </summary>
    [JsonPropertyName("telemetry")]
    public TelemetryConfig? Telemetry { get; set; }

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
