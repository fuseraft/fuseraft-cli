using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Terminates gracefully once cumulative session token usage reaches a threshold — a
/// softer alternative to <c>OrchestrationConfig.MaxTotalTokens</c>, which aborts the
/// session with a <c>BudgetExceededException</c> when exceeded. Pair this inside a
/// <c>composite</c> strategy with a <c>MaxTokens</c> value lower than <c>MaxTotalTokens</c>
/// so the loop exits through its normal path — the last agent's message stands as the
/// final answer — before the hard abort ever fires.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Extensions.AI.ChatMessage"/> does not carry per-message token usage,
/// so this condition cannot compute its own total from <c>history</c> the way
/// <see cref="RegexTerminationCondition"/> or <see cref="StructuredTerminationCondition"/> do.
/// Instead <see cref="fuseraft.Orchestration.AgentOrchestrator"/> wires in a live reader over
/// its own cumulative-token counter via <see cref="SetTokenReader"/>. Before that reader is
/// wired, this condition never terminates.
/// </remarks>
internal sealed class TokenBudgetTerminationCondition : ITerminationCondition
{
    private readonly int _maxTokens;
    private Func<int>? _tokenReader;

    public TokenBudgetTerminationCondition(int maxTokens)
    {
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// Wires in a live reader over the orchestrator's cumulative token counter.
    /// Must be called before the orchestration loop begins.
    /// </summary>
    public void SetTokenReader(Func<int> reader) => _tokenReader = reader;

    public ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_tokenReader is not null && _tokenReader() >= _maxTokens);
}
