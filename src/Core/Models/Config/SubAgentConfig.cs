namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the model used by the REPL's Explore/Locate/Delegate sub-agents (see
/// <c>fuseraft.Infrastructure.Plugins.SubAgentPlugin</c>). Mirrors
/// <c>AgentConfig.SubAgentModel</c>, which does the same for orchestration agents
/// declared in an <c>orchestration.yaml</c>.
/// </summary>
public record SubAgentConfig
{
    /// <summary>
    /// Model alias or ID for sub-agent tool calls (e.g. a cheaper/faster model for cost
    /// control on /explore, /locate, and /delegate). Resolved the same way as any other
    /// <see cref="ModelConfig.ModelId"/> — provider, endpoint, and API key are
    /// auto-detected from the prefix, independent of the REPL's main provider settings.
    /// Defaults to the REPL's main chat model when null or empty.
    /// </summary>
    public string? Model { get; init; }
}
