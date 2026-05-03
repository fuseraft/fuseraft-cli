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

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }
}
