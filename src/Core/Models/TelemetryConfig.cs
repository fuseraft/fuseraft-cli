namespace fuseraft.Core.Models;

/// <summary>
/// Optional OpenTelemetry export settings. When present, fuseraft-cli creates a
/// <c>TracerProvider</c> and <c>MeterProvider</c> and exports spans and metrics to
/// the configured OTLP endpoint.
/// </summary>
public record TelemetryConfig
{
    /// <summary>
    /// OTLP gRPC endpoint for traces and metrics.
    /// Example: <c>"http://localhost:4317"</c>
    /// </summary>
    public string OtlpEndpoint { get; init; } = "http://localhost:4317";

    /// <summary>
    /// Service name reported in trace/metric attributes.
    /// Defaults to the orchestration <c>Name</c> field when omitted.
    /// </summary>
    public string? ServiceName { get; init; }
}
