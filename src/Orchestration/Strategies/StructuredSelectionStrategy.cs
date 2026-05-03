using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// JSON-condition-based agent selection strategy.
///
/// <para>
/// After each agent turn the strategy locates the most recent text-bearing assistant
/// message, tries to extract a JSON object from it, and evaluates the declared
/// <see cref="StructuredRoute"/> conditions in order. The first route whose condition
/// returns true (and whose <c>SourceAgents</c> restriction is satisfied) determines
/// the next agent.
/// </para>
///
/// <para>
/// When the response cannot be parsed as JSON, or when no condition matches, the
/// strategy re-invokes the last active agent with a correction message instructing it
/// to return a JSON object with the expected fields.  After
/// <see cref="MaxParseRetries"/> consecutive failures a
/// <see cref="ValidatorStuckException"/> is thrown and the session stops.
/// </para>
/// </summary>
public sealed class StructuredSelectionStrategy : IAgentSelector
{
    private readonly IReadOnlyList<RouteEntry> _routes;
    private readonly string _defaultAgentName;
    private readonly ILogger<StructuredSelectionStrategy> _logger;
    private IList<ChatMessage>? _history;

    private const int MaxParseRetries = 3;
    private (string? AgentName, int Count)? _parseFailure;

    /// <summary>A resolved route entry bundling runtime values.</summary>
    public sealed record RouteEntry(
        string AgentName,
        StructuredCondition Condition,
        IReadOnlyList<string>? SourceAgents);

    public StructuredSelectionStrategy(
        IReadOnlyList<RouteEntry> routes,
        string defaultAgentName,
        ILogger<StructuredSelectionStrategy>? logger = null)
    {
        _routes           = routes;
        _defaultAgentName = defaultAgentName;
        _logger           = logger
            ?? Microsoft.Extensions.Logging.Abstractions
                        .NullLogger<StructuredSelectionStrategy>.Instance;
    }

    /// <summary>
    /// Provides the shared history so the strategy can inject correction messages
    /// when the agent's last response is not parseable JSON or matches no condition.
    /// Must be called before the orchestration loop begins.
    /// </summary>
    public void SetHistory(IList<ChatMessage> history) => _history = history;

    public Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Find the last assistant text message.
        string? lastText    = null;
        string? lastAuthor  = null;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (msg.Role == ChatRole.User) break;
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(msg.Text))
            {
                lastText   = msg.Text;
                lastAuthor = msg.AuthorName;
                break;
            }
        }

        // If there is no agent message yet, start with the default agent.
        if (lastText is null)
        {
            _logger.LogDebug("[Structured] No prior agent message — selecting default agent '{Default}'",
                _defaultAgentName);
            return Task.FromResult(FindAgent(agents, _defaultAgentName));
        }

        // Try to extract a JSON object from the response text.
        if (!TryExtractJson(lastText, out var doc) || doc is null)
        {
            _logger.LogDebug("[Structured] Response from '{Author}' is not valid JSON — injecting correction",
                lastAuthor ?? "(unknown)");
            return Task.FromResult(HandleParseFailure(agents, lastAuthor, isParseFail: true));
        }

        using (doc)
        {
            var root = doc.RootElement;

            foreach (var route in _routes)
            {
                // Source-agent restriction.
                if (route.SourceAgents is { Count: > 0 } &&
                    !route.SourceAgents.Any(s =>
                        string.Equals(s, lastAuthor, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug(
                        "[Structured] Route→'{Agent}' skipped: SourceAgents [{Allowed}] does not include '{Author}'",
                        route.AgentName, string.Join(",", route.SourceAgents), lastAuthor ?? "(null)");
                    continue;
                }

                bool matched = EvaluateCondition(root, route.Condition);

                _logger.LogDebug(
                    "[Structured] Condition Field='{Field}' on route→'{Agent}': {Result}",
                    route.Condition.Field, route.AgentName, matched ? "MATCH" : "no match");

                if (!matched) continue;

                var target = FindAgent(agents, route.AgentName);
                if (target is null)
                {
                    _logger.LogDebug("[Structured] Target agent '{Agent}' not found in pool — skipping",
                        route.AgentName);
                    continue;
                }

                // Reset failure counter on a successful match.
                _parseFailure = null;

                // Inject a turn-boundary marker when transitioning to a different agent.
                if (_history is not null &&
                    !string.Equals(lastAuthor, route.AgentName, StringComparison.OrdinalIgnoreCase))
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {lastAuthor} → {route.AgentName}]"));
                }

                _logger.LogDebug("[Structured] Route fired → '{Agent}'", route.AgentName);
                return Task.FromResult<AIAgent?>(target);
            }
        }

        // No condition matched.
        _logger.LogDebug("[Structured] No condition matched for response from '{Author}' — injecting correction",
            lastAuthor ?? "(unknown)");
        return Task.FromResult(HandleParseFailure(agents, lastAuthor, isParseFail: false));
    }

    // Helpers

    private AIAgent? HandleParseFailure(
        IReadOnlyList<AIAgent> agents,
        string? lastAuthor,
        bool isParseFail)
    {
        var agentKey = lastAuthor ?? _defaultAgentName;
        var newCount = (_parseFailure?.AgentName == agentKey)
            ? _parseFailure.Value.Count + 1
            : 1;
        _parseFailure = (agentKey, newCount);

        if (newCount >= MaxParseRetries)
        {
            _parseFailure = null;
            throw new Core.Exceptions.ValidatorStuckException(
                agentName:           agentKey,
                validatorName:       "StructuredRouting",
                consecutiveFailures: newCount,
                lastValidatorError:  isParseFail
                    ? "Agent did not return valid JSON."
                    : "Agent returned JSON but no route condition matched.");
        }

        if (_history is not null)
        {
            var expectedFields = _routes
                .Select(r => $"\"{r.Condition.Field}\"")
                .Distinct()
                .ToList();

            string correction = isParseFail
                ? $"STRUCTURED ROUTING ERROR ({newCount}/{MaxParseRetries}): " +
                  $"Your last response was not a valid JSON object. " +
                  $"Your entire response must be a single JSON object. " +
                  $"Required field(s): {string.Join(", ", expectedFields)}. " +
                  $"Example: {{{string.Join(", ", expectedFields.Select(f => $"{f}: \"<value>\""))}}}"
                : $"STRUCTURED ROUTING ERROR ({newCount}/{MaxParseRetries}): " +
                  $"Your JSON response did not match any configured route. " +
                  $"Required field(s): {string.Join(", ", expectedFields)}. " +
                  $"Check the allowed values for those field(s) and return a corrected JSON object.";

            _history.Add(new ChatMessage(ChatRole.User, correction));
        }

        // Re-invoke the same agent.
        var lastAgent = agents.FirstOrDefault(a =>
            string.Equals(a.Name, lastAuthor, StringComparison.OrdinalIgnoreCase));
        return lastAgent ?? FindAgent(agents, _defaultAgentName) ?? (agents.Count > 0 ? agents[0] : null);
    }

    private static bool EvaluateCondition(JsonElement root, StructuredCondition condition) =>
        StructuredConditionEvaluator.EvaluateCondition(root, condition);

    private static bool TryExtractJson(string text, out JsonDocument? doc) =>
        StructuredConditionEvaluator.TryExtractJson(text, out doc);

    private static AIAgent? FindAgent(IReadOnlyList<AIAgent> agents, string name) =>
        agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}
