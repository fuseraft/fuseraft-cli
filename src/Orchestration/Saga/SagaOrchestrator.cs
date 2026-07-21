using System.Runtime.CompilerServices;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;

namespace fuseraft.Orchestration.Saga;

/// <summary>
/// Wraps an <see cref="IOrchestrator"/> with the saga (compensating rollback) pattern.
///
/// <para>
/// As the inner orchestrator streams messages, this class maintains a stack of
/// <c>(agentName, AgentState)</c> pairs representing completed steps. If the inner
/// orchestrator throws an unhandled exception, the stack is unwound in reverse order
/// and <see cref="ICompensatingAgent.CompensateAsync"/> is called for each step whose
/// agent has a registered compensator. Steps without a compensator are skipped silently.
/// </para>
///
/// <para>
/// Compensation failures are swallowed so that the stack always unwinds fully; the
/// original exception is re-thrown after compensation completes.
/// </para>
/// </summary>
public sealed class SagaOrchestrator(
    IOrchestrator inner,
    SagaConfig sagaConfig,
    IReadOnlyDictionary<string, ICompensatingAgent>? compensators = null,
    EventEmitter? eventEmitter = null) : IOrchestrator
{
    private readonly IReadOnlyDictionary<string, ICompensatingAgent> _compensators =
        compensators ?? new Dictionary<string, ICompensatingAgent>(StringComparer.OrdinalIgnoreCase);

    private string _sessionId = string.Empty;

    /// <inheritdoc/>
    public event Action<string>? AgentStarting
    {
        add    => inner.AgentStarting += value;
        remove => inner.AgentStarting -= value;
    }

    /// <inheritdoc/>
    public event Action<string, string, string?>? ToolCalling
    {
        add    => inner.ToolCalling += value;
        remove => inner.ToolCalling -= value;
    }

    /// <inheritdoc/>
    public event Action<string, int, int>? TokenBudgetWarning
    {
        add    => inner.TokenBudgetWarning += value;
        remove => inner.TokenBudgetWarning -= value;
    }

    /// <inheritdoc/>
    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        inner.SetSessionId(sessionId);
    }

    /// <inheritdoc/>
    public void SetResumeExecutorId(string? executorId) => inner.SetResumeExecutorId(executorId);

    /// <inheritdoc/>
    public string? ResolveResumeExecutorId(AgentMessage lastAssistantMessage) =>
        inner.ResolveResumeExecutorId(lastAssistantMessage);

    /// <inheritdoc/>
    public void SetResumeStateName(string? stateName) => inner.SetResumeStateName(stateName);

    /// <inheritdoc/>
    public void SetStructuredTask(TaskModel? model) => inner.SetStructuredTask(model);

    /// <inheritdoc/>
    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<AgentMessage>();
        var start    = DateTime.UtcNow;

        try
        {
            await foreach (var msg in StreamAsync(task, priorHistory, cancellationToken).ConfigureAwait(false))
                messages.Add(msg);

            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = true,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "BudgetExceeded",
                ErrorMessage      = ex.Message
            };
        }
        catch (OperationCanceledException)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Cancelled",
                ErrorMessage      = "Operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Error",
                ErrorMessage      = ex.Message
            };
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AgentMessage> StreamAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var executedSteps = new Stack<(string AgentName, AgentState State)>();
        var currentState  = AgentState.Initial("saga");
        string? lastAgentName = null;

        Exception? failure = null;

        var enumerator = inner.StreamAsync(task, priorHistory, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using var _ = enumerator.ConfigureAwait(false);

        while (true)
        {
            AgentMessage? msg = null;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                msg = enumerator.Current;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            // Track agent transitions for the unwind stack.
            if (msg.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(msg.AgentName))
            {
                if (lastAgentName is not null
                    && !string.Equals(lastAgentName, msg.AgentName, StringComparison.OrdinalIgnoreCase))
                {
                    executedSteps.Push((lastAgentName, currentState));
                    currentState = StateHandoff.Advance(currentState, msg.AgentName);
                }
                lastAgentName = msg.AgentName;
            }

            yield return msg;
        }

        // Record the final agent that was running when the stream ended.
        if (lastAgentName is not null)
            executedSteps.Push((lastAgentName, currentState));

        if (failure is not null)
        {
            await RunCompensationAsync(executedSteps, cancellationToken).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task RunCompensationAsync(
        Stack<(string AgentName, AgentState State)> executedSteps,
        CancellationToken ct)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.SagaCompensating,
                payload: new { steps = executedSteps.Count, max = sagaConfig.MaxCompensationSteps });

        int compensated = 0;

        while (executedSteps.Count > 0 && compensated < sagaConfig.MaxCompensationSteps)
        {
            var (agentName, state) = executedSteps.Pop();

            if (!_compensators.TryGetValue(agentName, out var compensator))
                continue;

            try
            {
                await compensator.CompensateAsync(state, ct).ConfigureAwait(false);
                compensated++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.SagaCompensated,
                        agent:   agentName,
                        payload: new { version = state.Version });
            }
            catch
            {
                // Compensation failures are swallowed; the original failure is re-thrown upstream.
            }
        }
    }
}
