using System.Diagnostics.Metrics;
using fuseraft.Cli.Telemetry;
using fuseraft.Core.Events;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Orchestration.Hooks;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="TelemetryEventHook"/> — the bridge from <see cref="EventEmitter"/>'s
/// event stream to <see cref="FuseraftTelemetry"/>'s OTel counters. Verifies actual recorded
/// measurements via a real <see cref="MeterListener"/> on the "fuseraft" meter (the same
/// mechanism a real OTel SDK/collector uses) rather than mocking <see cref="FuseraftTelemetry"/>,
/// which is a sealed class with no interface seam to mock against.
///
/// No prior coverage existed for this hook or for <see cref="FuseraftTelemetry"/> at all —
/// this file also serves as that first coverage, not just REPL-parity verification.
/// </summary>
public sealed class TelemetryEventHookTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
    private readonly string _eventsPath;
    private readonly FuseraftTelemetry _telemetry;
    private readonly EventEmitter _emitter;

    public TelemetryEventHookTests()
    {
        _eventsPath = Path.Combine(Path.GetTempPath(), "fuseraft_telemetry_hook_tests_" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "fuseraft")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, tags.ToArray())));
        _listener.Start();

        // A real, unreachable OTLP endpoint — FuseraftTelemetry.Create only builds the
        // provider/instruments; it never blocks on the endpoint being reachable, matching how
        // the SDK behaves in production when a collector is temporarily down.
        _telemetry = FuseraftTelemetry.Create(
            new TelemetryConfig { OtlpEndpoint = "http://127.0.0.1:4317" }, "test-orchestration")!;

        _emitter = new EventEmitter(_eventsPath);
        _emitter.RegisterHook(new TelemetryEventHook(_telemetry));
    }

    public void Dispose()
    {
        _listener.Dispose();
        _telemetry.Dispose();
        _emitter.Dispose();
        if (File.Exists(_eventsPath)) File.Delete(_eventsPath);
    }

    private bool HasMeasurement(string instrumentName, string? tagKey = null, object? tagValue = null) =>
        _measurements.Any(m => m.Instrument == instrumentName &&
            (tagKey is null || m.Tags.Any(t => t.Key == tagKey && Equals(t.Value, tagValue))));

    [Fact]
    public async Task Compaction_RecordsCompactionCounterWithReason()
    {
        await _emitter.EmitAsync(EventTypes.Compaction, payload: new { reason = "context_budget" });

        Assert.True(HasMeasurement("fuseraft.compaction.count", "reason", "context_budget"));
    }

    [Fact]
    public async Task RetryAttempt_RecordsRetryCounterWithAgentAndReason()
    {
        await _emitter.EmitAsync(EventTypes.RetryAttempt, agent: "planner", payload: new { reason = "validator_failed" });

        Assert.True(HasMeasurement("fuseraft.retry.attempts", "agent.name", "planner"));
    }

    [Fact]
    public async Task RetryScheduled_AlsoRecordsRetryCounter()
    {
        // RetryAttempt and RetryScheduled are two distinct EventTypes that both map to the
        // same counter (see TelemetryEventHook's switch) — cover both, not just one.
        await _emitter.EmitAsync(EventTypes.RetryScheduled, agent: "coder", payload: new { reason = "shell_fail" });

        Assert.True(HasMeasurement("fuseraft.retry.attempts", "agent.name", "coder"));
    }

    [Fact]
    public async Task RetryExhausted_RecordsRetryExhaustedCounter()
    {
        await _emitter.EmitAsync(EventTypes.RetryExhausted, agent: "reviewer", payload: new { reason = "max_retries" });

        Assert.True(HasMeasurement("fuseraft.retry.exhausted", "agent.name", "reviewer"));
    }

    [Fact]
    public async Task CircuitBreakerOpen_RecordsCircuitBreakerCounter()
    {
        await _emitter.EmitAsync(EventTypes.CircuitBreakerOpen);

        Assert.True(HasMeasurement("fuseraft.circuit_breaker.opens"));
    }

    [Fact]
    public async Task HitlEscalation_RecordsHitlEscalationCounterWithAgent()
    {
        await _emitter.EmitAsync(EventTypes.HitlEscalation, agent: "coder");

        Assert.True(HasMeasurement("fuseraft.hitl.escalations", "agent.name", "coder"));
    }

    [Fact]
    public async Task HitlRejected_RecordsHitlRejectedCounterWithAgent()
    {
        await _emitter.EmitAsync(EventTypes.HitlRejected, agent: "coder");

        Assert.True(HasMeasurement("fuseraft.hitl.rejections", "agent.name", "coder"));
    }

    [Fact]
    public async Task ContextBudgetWarn_RecordsContextBudgetWarnCounter()
    {
        await _emitter.EmitAsync(EventTypes.ContextBudgetWarn, agent: "planner");

        Assert.True(HasMeasurement("fuseraft.context_budget.warnings", "agent.name", "planner"));
    }

    [Fact]
    public async Task ContextBudgetCutover_RecordsContextBudgetCutoverCounter()
    {
        await _emitter.EmitAsync(EventTypes.ContextBudgetCutover, agent: "planner");

        Assert.True(HasMeasurement("fuseraft.context_budget.cutovers", "agent.name", "planner"));
    }

    [Fact]
    public async Task MaxTurnsExceeded_RecordsMaxTurnsExceededCounter()
    {
        await _emitter.EmitAsync(EventTypes.MaxTurnsExceeded);

        Assert.True(HasMeasurement("fuseraft.session.max_turns_exceeded"));
    }

    [Fact]
    public async Task UnrelatedEventType_RecordsNothing()
    {
        // ToolCall/ToolResult and friends are not in the hook's switch — they flow through
        // events.jsonl (and, separately, ToolResultLoggingFilter) but never touch a counter.
        await _emitter.EmitAsync(EventTypes.ToolCall, payload: new { tool_name = "shell_run" });

        Assert.Empty(_measurements);
    }

    [Fact]
    public async Task MissingReasonInPayload_RecordsCounterWithUnknownReason()
    {
        // Payload shapes vary by call site; a Compaction event with no `reason` property must
        // not throw — GetString falls back to "unknown" rather than crashing the hook.
        await _emitter.EmitAsync(EventTypes.Compaction, payload: new { unrelated_field = 1 });

        Assert.True(HasMeasurement("fuseraft.compaction.count", "reason", "unknown"));
    }

    [Fact]
    public async Task NullPayload_DoesNotThrow()
    {
        await _emitter.EmitAsync(EventTypes.RetryAttempt, agent: "agent-a", payload: null);

        Assert.True(HasMeasurement("fuseraft.retry.attempts", "reason", "unknown"));
    }
}
