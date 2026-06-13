namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Immutable snapshot of a structured orchestration event, passed to every registered
/// <see cref="fuseraft.Core.Interfaces.IOrchestrationHook"/> when the event is emitted.
///
/// <para>
/// Mirrors the JSONL schema written by <see cref="fuseraft.Orchestration.EventEmitter"/>:
/// <c>{ ts, session, agent, turn, event_type, payload }</c>
/// </para>
///
/// <para>
/// Built-in event types (see design.md §14 for the full list):
/// <list type="table">
///   <item><term><c>turn_end</c></term><description>Agent turn completed. Payload: <c>{ agent, turn, input_tokens, output_tokens }</c></description></item>
///   <item><term><c>validation_fail</c></term><description>Routing validator blocked a handoff. Payload varies by strategy: keyword/workflow — <c>{ validator, consecutive }</c>; state machine — <c>{ contract, state, transition, consecutive, error }</c></description></item>
///   <item><term><c>hitl_escalation</c></term><description>Stuck validator forced human-in-the-loop. Payload: <c>{ reason }</c></description></item>
///   <item><term><c>tool_blocked</c></term><description>Sandbox or governance kernel denied a tool call. Payload: <c>{ policy, data }</c></description></item>
///   <item><term><c>keyword_not_found</c></term><description>No route keyword matched after scanning the lookback window.</description></item>
///   <item><term><c>magentic_plan</c></term><description>Magentic manager produced an initial plan. Payload: <c>{ plan }</c></description></item>
///   <item><term><c>magentic_complete</c></term><description>Magentic inner loop completed. Payload: <c>{ rounds }</c></description></item>
/// </list>
/// </para>
/// </summary>
public record OrchestrationEvent(
    /// <summary>Event type string — filter on this in <see cref="fuseraft.Core.Interfaces.IOrchestrationHook.OnEventAsync"/>.</summary>
    string EventType,

    /// <summary>UTC timestamp of the event.</summary>
    DateTimeOffset Timestamp,

    /// <summary>Session ID, if set via <see cref="fuseraft.Orchestration.EventEmitter.SetSessionId"/>.</summary>
    string? SessionId,

    /// <summary>Name of the agent that caused the event, when applicable.</summary>
    string? Agent,

    /// <summary>Turn index within the session, when applicable.</summary>
    int? Turn,

    /// <summary>
    /// Event-specific payload. Cast to the expected anonymous type or use reflection/JSON
    /// serialization to extract fields. Null for events with no additional data.
    /// </summary>
    object? Payload);
