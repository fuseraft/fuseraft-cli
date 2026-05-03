namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Implemented by plugins that maintain turn-scoped state (e.g. a read cache) that must
/// be cleared at the start of each agent turn.  <see cref="AgentFactory.OnAgentTurnStarting"/>
/// calls <see cref="BeginTurn"/> on every registered implementor before the agent runs.
/// </summary>
internal interface ITurnResettable
{
    void BeginTurn();
}
