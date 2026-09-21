using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the model, round caps and wall-clock timeouts of the REPL's Explore/Locate/Delegate
/// subagents (see <c>fuseraft.Infrastructure.Plugins.SubagentPlugin</c>). Mirrors
/// <c>AgentConfig.SubagentModel</c> and <c>AgentConfig.SubagentMaxToolCalls</c>, which do the
/// same for orchestration agents declared in an <c>orchestration.yaml</c>.
/// </summary>
public record SubagentConfig
{
    public const int MaxTimeoutMinutes = 240;

    /// <summary>
    /// Model alias or ID for subagent tool calls (e.g. a cheaper/faster model for cost
    /// control on /explore, /locate, and /delegate). Resolved by <c>ReplFactory.ResolveOverrideModel</c>:
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

    /// <summary>
    /// Round cap for /explore (a round is one model call inside the subagent's loop; parallel tool
    /// calls in the same round count once). Null uses the built-in default of 20.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExploreMaxIterations { get; init; }

    /// <summary>Round cap for /delegate. Null uses the built-in default of 40.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelegateMaxIterations { get; init; }

    /// <summary>Wall-clock limit for /explore in minutes. Null uses the built-in default of 8.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExploreTimeoutMinutes { get; init; }

    /// <summary>Wall-clock limit for /locate in minutes. Null uses the built-in default of 2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LocateTimeoutMinutes { get; init; }

    /// <summary>Wall-clock limit for /delegate in minutes. Null uses the built-in default of 15.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelegateTimeoutMinutes { get; init; }

    /// <summary>True when no field is set, so the whole section can be dropped from the config file.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Provider)
        && string.IsNullOrWhiteSpace(Endpoint) && string.IsNullOrWhiteSpace(ApiKeyEnvVar)
        && ExploreMaxIterations is null && DelegateMaxIterations is null
        && ExploreTimeoutMinutes is null && LocateTimeoutMinutes is null && DelegateTimeoutMinutes is null;
}
