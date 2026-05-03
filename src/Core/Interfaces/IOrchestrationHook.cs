using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Receives structured orchestration events as they are emitted during a session.
///
/// <para>
/// Implementations are called synchronously (awaited) inside <see cref="fuseraft.Orchestration.EventEmitter.EmitAsync"/>
/// after the JSONL line is written to disk. They share the same best-effort contract as
/// the file write: exceptions thrown by a hook are swallowed so a misbehaving hook cannot
/// disrupt the orchestration session.
/// </para>
///
/// <para>
/// Use hooks to build feedback loops that the write-only event log cannot support:
/// <list type="bullet">
///   <item>Inject diagnostic context into the agent history on <c>validation_fail</c>.</item>
///   <item>Post real-time alerts to Slack, PagerDuty, or a webhook.</item>
///   <item>Push metrics to Prometheus, DataDog, or a custom dashboard.</item>
///   <item>Trigger a secondary monitoring or auditing agent.</item>
/// </list>
/// </para>
///
/// <para>
/// Register hooks via <see cref="fuseraft.Orchestration.EventEmitter.RegisterHook"/>.
/// Hooks receive all event types; filter on <see cref="OrchestrationEvent.EventType"/>
/// to react only to the events relevant to the hook's purpose.
/// </para>
/// </summary>
public interface IOrchestrationHook
{
    /// <summary>
    /// Called when a structured event is emitted during the orchestration session.
    /// Must not throw — exceptions are swallowed by the event emitter.
    /// </summary>
    Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default);
}
