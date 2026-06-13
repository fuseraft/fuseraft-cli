namespace fuseraft.Core.Models.Agents;

/// <summary>
/// Configures an agent that is hosted remotely and accessed via the A2A protocol.
/// When present on an <see cref="AgentConfig"/>, <c>AgentFactory</c> resolves the
/// remote agent card from <see cref="Url"/> and wraps it as a local <see cref="Microsoft.Agents.AI.AIAgent"/>.
/// The agent participates in orchestration identically to locally-hosted agents.
/// </summary>
public record RemoteAgentConfig
{
    /// <summary>
    /// Base URL of the remote A2A agent. The agent card is fetched from
    /// <c>{Url}/.well-known/agent.json</c> at session startup.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// HTTP timeout in seconds for card resolution and per-turn calls.
    /// Defaults to 120 s to accommodate long-running remote agent turns.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;
}
