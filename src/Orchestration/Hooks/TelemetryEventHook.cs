using fuseraft.Cli.Telemetry;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Hooks;

/// <summary>
/// An <see cref="IOrchestrationHook"/> that forwards a curated subset of orchestration
/// events into OTel counters via <see cref="FuseraftTelemetry"/>.
///
/// <para>
/// This is the single integration point between the write-only <c>events.jsonl</c> log
/// and the metrics backend — reliability signals (retries, circuit breaker trips, HITL
/// escalations, budget cutovers, compaction) are raised from many call sites across the
/// orchestrators, but all of them already flow through <see cref="EventEmitter"/>, so one
/// hook here covers them instead of instrumenting each call site directly.
/// </para>
/// </summary>
public sealed class TelemetryEventHook(FuseraftTelemetry telemetry) : IOrchestrationHook
{
    public Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
    {
        var agent = evt.Agent ?? "unknown";

        switch (evt.EventType)
        {
            case EventTypes.Compaction:
                telemetry.RecordCompaction(GetString(evt.Payload, "reason") ?? "unknown");
                break;

            case EventTypes.RetryAttempt:
            case EventTypes.RetryScheduled:
                telemetry.RecordRetryAttempt(agent, GetString(evt.Payload, "reason") ?? "unknown");
                break;

            case EventTypes.RetryExhausted:
                telemetry.RecordRetryExhausted(agent, GetString(evt.Payload, "reason") ?? "unknown");
                break;

            case EventTypes.CircuitBreakerOpen:
                telemetry.RecordCircuitBreakerOpen();
                break;

            case EventTypes.HitlEscalation:
                telemetry.RecordHitlEscalation(agent);
                break;

            case EventTypes.HitlRejected:
                telemetry.RecordHitlRejected(agent);
                break;

            case EventTypes.ContextBudgetWarn:
                telemetry.RecordContextBudgetWarn(agent);
                break;

            case EventTypes.ContextBudgetCutover:
                telemetry.RecordContextBudgetCutover(agent);
                break;

            case EventTypes.MaxTurnsExceeded:
                telemetry.RecordMaxTurnsExceeded();
                break;
        }

        return Task.CompletedTask;
    }

    // Extracts a named string field from an anonymous event payload. Payload shapes vary by
    // call site (see EventTypes emission sites across the orchestrators), so fields are read
    // by name via reflection rather than a shared payload type.
    private static string? GetString(object? payload, string propertyName)
    {
        if (payload is null) return null;
        try
        {
            var prop = payload.GetType().GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(payload)?.ToString();
        }
        catch { return null; }
    }
}
