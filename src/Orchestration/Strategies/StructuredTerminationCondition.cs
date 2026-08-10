using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Terminates when the last agent text message contains a JSON object satisfying a
/// <see cref="StructuredCondition"/> — e.g. <c>{"status": "done"}</c> — rather than
/// requiring the agent to emit a specific keyword for <see cref="RegexTerminationCondition"/>
/// to match. Shares its condition evaluation with <see cref="StructuredSelectionStrategy"/>
/// via <see cref="StructuredConditionEvaluator"/>.
/// </summary>
internal sealed class StructuredTerminationCondition : ITerminationCondition
{
    private readonly StructuredCondition _condition;
    private readonly IReadOnlyList<string>? _agentNames;

    public StructuredTerminationCondition(StructuredCondition condition, IReadOnlyList<string>? agentNames = null)
    {
        _condition  = condition;
        _agentNames = agentNames;
    }

    public ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Scan backward for the last assistant message that carries text.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant) continue;

            // If agent-name filter is set, skip messages from other agents.
            if (_agentNames is { Count: > 0 } &&
                !_agentNames.Any(n => string.Equals(n, msg.AuthorName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // No text yet from this agent — keep scanning earlier messages.
            if (string.IsNullOrEmpty(msg.Text)) continue;

            if (!StructuredConditionEvaluator.TryExtractJson(msg.Text, out var doc) || doc is null)
                return ValueTask.FromResult(false);

            using (doc)
                return ValueTask.FromResult(StructuredConditionEvaluator.EvaluateCondition(doc.RootElement, _condition));
        }

        return ValueTask.FromResult(false);
    }
}
