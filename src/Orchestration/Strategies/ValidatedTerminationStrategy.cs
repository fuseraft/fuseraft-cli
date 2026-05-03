using AgentGovernance;
using AgentGovernance.Audit;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Wraps any <see cref="ITerminationCondition"/> with a pre-termination validator.
///
/// <para>
/// When the inner condition would fire (e.g. the regex matched <c>APPROVED</c>), this
/// wrapper runs an <see cref="IRoutingValidator"/> first. If validation fails:
/// <list type="bullet">
///   <item>The error message is injected into the shared history as a user message so the
///       agent sees it on its next turn.</item>
///   <item>Termination is blocked — the conversation continues.</item>
/// </list>
/// The agent must address the error (e.g. run a verification command) and re-emit the
/// termination keyword before the session can end.
/// </para>
/// </summary>
public sealed class ValidatedTerminationStrategy : ITerminationCondition
{
    private readonly ITerminationCondition _inner;
    private readonly IReadOnlyList<IRoutingValidator> _validators;
    private readonly GovernanceKernel? _governance;
    private IList<ChatMessage>? _history;
    private string _sessionId = "unknown";
    private Func<string, string>? _didResolver;

    public ValidatedTerminationStrategy(ITerminationCondition inner, IRoutingValidator validator, GovernanceKernel? governanceKernel = null)
        : this(inner, [validator], governanceKernel) { }

    public ValidatedTerminationStrategy(ITerminationCondition inner, IReadOnlyList<IRoutingValidator> validators, GovernanceKernel? governanceKernel = null)
    {
        _inner      = inner;
        _validators = validators;
        _governance = governanceKernel;
    }

    /// <summary>
    /// Provides the shared history used to inject error messages when validation fails.
    /// Must be called before the orchestration loop begins.
    /// </summary>
    public void SetHistory(IList<ChatMessage> history) => _history = history;

    /// <summary>Stamps all governance audit events with this session ID for correlation.</summary>
    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>Provides a function that resolves an agent name to its DID for audit correlation.</summary>
    public void SetDidResolver(Func<string, string> resolver) => _didResolver = resolver;

    public async ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Ask the inner condition if termination would occur.
        if (!await _inner.ShouldTerminateAsync(history, cancellationToken))
            return false;

        // Inner wants to terminate — run all validators (AND semantics).
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(history, cancellationToken);
            if (result.IsValid) continue;

            // Validation failed. Inject the error so the agent corrects before trying again.
            if (_history is not null && result.ErrorMessage is not null)
                _history.Add(new ChatMessage(ChatRole.User, result.ErrorMessage));

            if (_governance is not null)
            {
                var lastAgent = history.LastOrDefault(
                    m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.AuthorName))
                    ?.AuthorName ?? "termination-guard";
                var agentDid = _didResolver?.Invoke(lastAgent) ?? lastAgent;

                _governance.AuditEmitter.Emit(
                    GovernanceEventType.PolicyViolation,
                    agentId:   agentDid,
                    sessionId: _sessionId,
                    data: new Dictionary<string, object>
                    {
                        ["agent_name"] = lastAgent,
                        ["validator"]  = validator.GetType().Name,
                        ["error"]      = result.ErrorMessage ?? string.Empty,
                    });
            }

            return false;
        }

        return true;
    }
}
