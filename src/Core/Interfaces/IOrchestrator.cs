using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Runs a multi-agent conversation given a task description.
/// </summary>
public interface IOrchestrator
{
    /// <summary>
    /// Runs the full orchestration and returns the aggregated result.
    /// Suitable for non-interactive / batch use.
    /// </summary>
    Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams agent messages as they arrive in real time.
    /// When <paramref name="priorHistory"/> is supplied the prior conversation is
    /// re-injected into the group chat so agents continue from where they left off.
    /// </summary>
    IAsyncEnumerable<AgentMessage> StreamAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the session ID on the orchestrator so governance audit events carry a
    /// correlation ID. Called by the CLI after the checkpoint session ID is known.
    /// </summary>
    void SetSessionId(string sessionId);

    /// <summary>
    /// Provides an explicit executor ID hint for the next <see cref="StreamAsync"/> call.
    /// When set, <see cref="StreamAsync"/> starts from this executor regardless of what
    /// keyword scanning of prior history would otherwise infer.
    /// Used by the CLI after compaction to ensure restarts resume from the correct agent.
    /// The hint is consumed once and cleared after first use.
    /// Defaults to a no-op; only <c>WorkflowOrchestrator</c> needs to override it.
    /// </summary>
    void SetResumeExecutorId(string? executorId) { }

    /// <summary>
    /// Provides an explicit state machine state name for the next <see cref="StreamAsync"/> call.
    /// Used after compaction to restore the <c>StateMachineSelectionStrategy</c> to the state
    /// it was in before the history was trimmed, preventing a spurious reset to the initial
    /// state (e.g. Planning) when the machine was actually in e.g. Testing.
    /// The hint is consumed once on the next StreamAsync call.
    /// Defaults to a no-op; only <c>AgentOrchestrator</c> with a state machine strategy uses it.
    /// </summary>
    void SetResumeStateName(string? stateName) { }

    /// <summary>
    /// Sets the structured task model to inject into history at the next <see cref="StreamAsync"/>
    /// call, giving agents a typed view of the goal, constraints, and active file targets.
    /// When null (default), no task model block is injected.
    /// Defaults to a no-op; override in orchestrators that support context projection.
    /// </summary>
    void SetStructuredTask(fuseraft.Core.Models.TaskModel? model) { }

    /// <summary>
    /// Fires synchronously when an agent is selected but before its turn begins.
    /// Used to update UI status spinners before a potentially long-running LLM call.
    /// </summary>
    event Action<string>? AgentStarting;

    /// <summary>
    /// Fires synchronously each time an agent invokes a tool during its turn.
    /// Arguments: (agentName, toolName, argsSummary) where <c>argsSummary</c> is a compact
    /// <c>key=value</c> string produced by <see cref="fuseraft.Infrastructure.ToolCallHelper.SummarizeArgs"/>,
    /// or <c>null</c> when the tool was called with no arguments.
    /// Used to update UI spinners and print real-time tool-call lines.
    /// </summary>
    event Action<string, string, string?>? ToolCalling;

    /// <summary>
    /// Fires after an agent turn completes when the agent's input token count for that
    /// turn exceeded the configured warning threshold (see <c>WarnTurnTokens</c>).
    /// Arguments: (agentName, inputTokens, warnThreshold).
    /// Used to surface early warnings before a token-budget blowup occurs.
    /// </summary>
    event Action<string, int, int>? TokenBudgetWarning;
}
