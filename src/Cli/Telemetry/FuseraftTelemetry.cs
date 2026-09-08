using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Telemetry;

/// <summary>
/// Manages OpenTelemetry <see cref="TracerProvider"/> and <see cref="MeterProvider"/>
/// for a fuseraft session. Create via <see cref="Create"/> and dispose at session end.
/// </summary>
public sealed class FuseraftTelemetry : IDisposable
{
    private const string MeterName    = "fuseraft";
    private const string ActivityName = "fuseraft";

    private readonly TracerProvider  _tracerProvider;
    private readonly MeterProvider   _meterProvider;
    private readonly Meter           _meter;
    private readonly ActivitySource  _activitySource;

    // Instruments
    private readonly Counter<long>      _turnCounter;
    private readonly Counter<long>      _inputTokenCounter;
    private readonly Counter<long>      _outputTokenCounter;
    private readonly Histogram<double>  _durationHistogram;
    private readonly Counter<long>      _toolCallCounter;
    private readonly Counter<long>      _sessionCounter;
    private readonly Histogram<double>  _sessionDurationHistogram;
    private readonly Counter<long>      _compactionCounter;
    private readonly Counter<long>      _retryCounter;
    private readonly Counter<long>      _retryExhaustedCounter;
    private readonly Counter<long>      _circuitBreakerCounter;
    private readonly Counter<long>      _hitlEscalationCounter;
    private readonly Counter<long>      _hitlRejectedCounter;
    private readonly Counter<long>      _contextBudgetWarnCounter;
    private readonly Counter<long>      _contextBudgetCutoverCounter;
    private readonly Counter<long>      _maxTurnsExceededCounter;

    private FuseraftTelemetry(
        TracerProvider  tracerProvider,
        MeterProvider   meterProvider,
        Meter           meter,
        ActivitySource  activitySource)
    {
        _tracerProvider = tracerProvider;
        _meterProvider  = meterProvider;
        _meter          = meter;
        _activitySource = activitySource;

        _turnCounter        = _meter.CreateCounter<long>  ("fuseraft.agent.turns",             description: "Number of agent turns completed.");
        _inputTokenCounter  = _meter.CreateCounter<long>  ("fuseraft.tokens.input",             description: "Total input tokens consumed.");
        _outputTokenCounter = _meter.CreateCounter<long>  ("fuseraft.tokens.output",            description: "Total output tokens produced.");
        _durationHistogram  = _meter.CreateHistogram<double>("fuseraft.agent.duration_seconds", unit: "s",   description: "Wall-clock seconds per agent turn.");

        _toolCallCounter             = _meter.CreateCounter<long>  ("fuseraft.tool.calls",                    description: "Number of tool calls made, by tool name and outcome.");
        _sessionCounter              = _meter.CreateCounter<long>  ("fuseraft.session.completed",             description: "Number of sessions completed, by outcome.");
        _sessionDurationHistogram    = _meter.CreateHistogram<double>("fuseraft.session.duration_seconds", unit: "s", description: "Wall-clock seconds per session.");
        _compactionCounter           = _meter.CreateCounter<long>  ("fuseraft.compaction.count",              description: "Number of conversation compaction cycles, by trigger reason.");
        _retryCounter                = _meter.CreateCounter<long>  ("fuseraft.retry.attempts",                description: "Number of agent-turn retries scheduled, by agent and reason.");
        _retryExhaustedCounter       = _meter.CreateCounter<long>  ("fuseraft.retry.exhausted",                description: "Number of times an agent's retry budget was exhausted.");
        _circuitBreakerCounter       = _meter.CreateCounter<long>  ("fuseraft.circuit_breaker.opens",          description: "Number of times the provider circuit breaker tripped open.");
        _hitlEscalationCounter       = _meter.CreateCounter<long>  ("fuseraft.hitl.escalations",                description: "Number of human-in-the-loop escalations.");
        _hitlRejectedCounter         = _meter.CreateCounter<long>  ("fuseraft.hitl.rejections",                 description: "Number of human-in-the-loop rejections.");
        _contextBudgetWarnCounter    = _meter.CreateCounter<long>  ("fuseraft.context_budget.warnings",        description: "Number of context token budget warnings.");
        _contextBudgetCutoverCounter = _meter.CreateCounter<long>  ("fuseraft.context_budget.cutovers",        description: "Number of context token budget cutovers into compaction.");
        _maxTurnsExceededCounter     = _meter.CreateCounter<long>  ("fuseraft.session.max_turns_exceeded",     description: "Number of times a session hit its max-turns/max-iterations cap.");
    }

    /// <summary>
    /// Creates a <see cref="FuseraftTelemetry"/> instance configured to export to
    /// <paramref name="cfg"/>. Returns <c>null</c> if <paramref name="cfg"/> is <c>null</c>.
    /// </summary>
    public static FuseraftTelemetry? Create(TelemetryConfig? cfg, string orchestrationName)
    {
        if (cfg is null) return null;

        var serviceName = cfg.ServiceName ?? orchestrationName;
        var resource    = ResourceBuilder.CreateDefault()
            .AddService(serviceName);

        var tracer = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(ActivityName)
            .AddSource("Microsoft.Agents.AI*")
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = new Uri(cfg.OtlpEndpoint))
            .Build()!;

        var meter = new Meter(MeterName);

        var meterProv = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(MeterName)
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = new Uri(cfg.OtlpEndpoint))
            .Build()!;

        var activitySource = new ActivitySource(ActivityName);

        return new FuseraftTelemetry(tracer, meterProv, meter, activitySource);
    }

    /// <summary>
    /// Records metrics for a completed agent turn and emits an OTel span.
    /// </summary>
    /// <param name="msg">The agent message produced this turn.</param>
    /// <param name="elapsed">Wall-clock duration of the turn.</param>
    /// <param name="modelId">Optional model ID for the <c>model.id</c> tag.</param>
    public void RecordTurn(AgentMessage msg, TimeSpan elapsed, string? modelId = null)
    {
        var tags = new TagList
        {
            { "agent.name", msg.AgentName },
            { "model.id",   modelId ?? "unknown" }
        };

        _turnCounter.Add(1, tags);
        _durationHistogram.Record(elapsed.TotalSeconds, tags);

        if (msg.Usage is { } u)
        {
            _inputTokenCounter.Add(u.InputTokens,  tags);
            _outputTokenCounter.Add(u.OutputTokens, tags);
        }

        if (msg.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                _toolCallCounter.Add(1, new TagList
                {
                    { "agent.name",   msg.AgentName },
                    { "tool.name",    tc.Name },
                    { "tool.success", tc.Succeeded },
                });
            }
        }

        using var activity = _activitySource.StartActivity(
            $"agent.turn/{msg.AgentName}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("agent.name",       msg.AgentName);
            activity.SetTag("model.id",         modelId ?? "unknown");
            activity.SetTag("turn.index",       msg.TurnIndex);
            activity.SetTag("tokens.input",     msg.Usage?.InputTokens ?? 0);
            activity.SetTag("tokens.output",    msg.Usage?.OutputTokens ?? 0);
            activity.SetTag("duration_seconds", elapsed.TotalSeconds);
        }
    }

    /// <summary>Records the terminal outcome and total wall-clock duration of a session.</summary>
    public void RecordSession(bool succeeded, TimeSpan elapsed)
    {
        var tags = new TagList { { "succeeded", succeeded } };
        _sessionCounter.Add(1, tags);
        _sessionDurationHistogram.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Records a conversation-compaction cycle triggered for <paramref name="reason"/>.</summary>
    public void RecordCompaction(string reason) =>
        _compactionCounter.Add(1, new TagList { { "reason", reason } });

    /// <summary>Records a retry scheduled for <paramref name="agent"/> due to <paramref name="reason"/>.</summary>
    public void RecordRetryAttempt(string agent, string reason) =>
        _retryCounter.Add(1, new TagList { { "agent.name", agent }, { "reason", reason } });

    /// <summary>Records that <paramref name="agent"/>'s retry budget was exhausted for <paramref name="reason"/>.</summary>
    public void RecordRetryExhausted(string agent, string reason) =>
        _retryExhaustedCounter.Add(1, new TagList { { "agent.name", agent }, { "reason", reason } });

    /// <summary>Records the provider circuit breaker tripping open.</summary>
    public void RecordCircuitBreakerOpen() => _circuitBreakerCounter.Add(1);

    /// <summary>Records a human-in-the-loop escalation for <paramref name="agent"/>.</summary>
    public void RecordHitlEscalation(string agent) =>
        _hitlEscalationCounter.Add(1, new TagList { { "agent.name", agent } });

    /// <summary>Records a human-in-the-loop rejection for <paramref name="agent"/>.</summary>
    public void RecordHitlRejected(string agent) =>
        _hitlRejectedCounter.Add(1, new TagList { { "agent.name", agent } });

    /// <summary>Records a context token budget warning for <paramref name="agent"/>.</summary>
    public void RecordContextBudgetWarn(string agent) =>
        _contextBudgetWarnCounter.Add(1, new TagList { { "agent.name", agent } });

    /// <summary>Records a context token budget cutover (forced compaction) for <paramref name="agent"/>.</summary>
    public void RecordContextBudgetCutover(string agent) =>
        _contextBudgetCutoverCounter.Add(1, new TagList { { "agent.name", agent } });

    /// <summary>Records a session hitting its max-turns/max-iterations cap.</summary>
    public void RecordMaxTurnsExceeded() => _maxTurnsExceededCounter.Add(1);

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }
}
