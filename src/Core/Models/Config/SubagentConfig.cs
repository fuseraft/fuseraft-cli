using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the model and round caps of the REPL's Explore/Locate/Delegate subagents (see
/// <c>fuseraft.Infrastructure.Plugins.SubagentPlugin</c>). Mirrors
/// <c>AgentConfig.SubagentModel</c> and <c>AgentConfig.SubagentMaxToolCalls</c>, which do the
/// same for orchestration agents declared in an <c>orchestration.yaml</c>.
/// </summary>
public record SubagentConfig
{
    /// <summary>
    /// Model alias or ID for subagent tool calls (e.g. a cheaper/faster model for cost
    /// control on /explore, /locate, and /delegate). Resolved the same way as any other
    /// <see cref="ModelConfig.ModelId"/> — provider, endpoint, and API key are
    /// auto-detected from the prefix, independent of the REPL's main provider settings.
    /// Defaults to the REPL's main chat model when null or empty.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Round cap for /explore (a round is one model call inside the subagent's loop; parallel tool
    /// calls in the same round count once). Null uses the built-in default of 20.
    /// </summary>
    public int? ExploreMaxIterations { get; init; }

    /// <summary>Round cap for /delegate. Null uses the built-in default of 40.</summary>
    public int? DelegateMaxIterations { get; init; }

    /// <summary>True when no field is set, so the whole section can be dropped from the config file.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Model) && ExploreMaxIterations is null && DelegateMaxIterations is null;
}
