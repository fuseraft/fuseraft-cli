using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Session;

public sealed record InvestigationLog
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("hypotheses")]
    public List<HypothesisRecord> Hypotheses { get; init; } = [];

    [JsonPropertyName("investigations")]
    public List<InvestigationRecord> Investigations { get; init; } = [];

    [JsonPropertyName("confirmedRootCauses")]
    public List<string> ConfirmedRootCauses { get; init; } = [];
}

public sealed record HypothesisRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("hypothesis")]
    public string Hypothesis { get; init; } = string.Empty;

    /// <summary>"open" | "confirmed" | "rejected"</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("rejectReason")]
    public string? RejectReason { get; init; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; init; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record InvestigationRecord
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}
