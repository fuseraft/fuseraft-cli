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

    /// <summary>
    /// Overrides the REPL's heuristic working-context-token budget (<see cref="fuseraft.Cli.Commands.Repl.ModelContextWindow"/>)
    /// used for history trimming and the /context, /compact, and context-warning displays.
    /// REPL-only — unrelated to the orchestration-level <c>ContextBudgetConfig</c>
    /// (warn/cutover/tool-result trimming for agent orchestration runs); the similar name is
    /// coincidental, hence the <c>Repl</c> prefix here to keep the two unambiguous.
    /// Applies to every model used in the REPL session, regardless of model family. Null or
    /// &lt;= 0 falls back to the built-in per-family heuristic.
    /// </summary>
    [JsonPropertyName("replContextBudget")]
    public int? ReplContextBudget { get; set; }

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
