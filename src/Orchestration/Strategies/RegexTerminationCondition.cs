using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Orchestration.Strategies;

/// <summary>Terminates when a regex pattern matches the last agent text message.</summary>
internal sealed class RegexTerminationCondition : ITerminationCondition
{
    private readonly Regex _regex;
    private readonly IReadOnlyList<string>? _agentNames;

    public RegexTerminationCondition(string pattern, IReadOnlyList<string>? agentNames = null)
    {
        _regex      = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        _agentNames = agentNames;
    }

    public ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Scan backward for the last assistant message from the relevant agent —
        // checking both plain text and HandoffPlugin tool-call arguments.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant) continue;

            // If agent-name filter is set, skip messages from other agents.
            if (_agentNames is { Count: > 0 } &&
                !_agentNames.Any(n => string.Equals(n, msg.AuthorName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Plain text takes precedence.
            if (!string.IsNullOrEmpty(msg.Text))
                return ValueTask.FromResult(_regex.IsMatch(msg.Text));

            // Also match against HandoffPlugin tool-call arguments so that
            // handoff(route_keyword: "KEYWORD") is treated identically to emitting
            // the keyword as text.
            foreach (var item in msg.Contents)
            {
                if (item is FunctionCallContent fc
                    && string.Equals(fc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)
                    && fc.Arguments?.TryGetValue(HandoffPlugin.ArgumentName, out var kwObj) == true
                    && kwObj?.ToString() is { Length: > 0 } kw)
                {
                    return ValueTask.FromResult(_regex.IsMatch(kw));
                }
            }
            // No text and no handoff call — keep scanning earlier messages.
        }

        return ValueTask.FromResult(false);
    }
}
