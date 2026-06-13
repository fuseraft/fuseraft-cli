namespace fuseraft.Core.Models.Repository;

/// <summary>
/// A durable, entity-scoped finding extracted from agent observations and persisted across
/// sessions in <c>.fuseraft/state/knowledge_findings.json</c>.
///
/// <para>
/// Unlike <see cref="RepositoryMemoryEntry"/> (which stores approved patterns) or ADR records
/// (which store architectural decisions), a <see cref="RepositoryKnowledgeFinding"/> captures
/// ground-truth facts discovered during tool use — file ownership, dependency relationships,
/// known pitfalls, and code observations that future agents can retrieve by entity name.
/// </para>
/// </summary>
public sealed record RepositoryKnowledgeFinding
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>The entity this finding concerns — a file path, symbol name, service name, etc.</summary>
    public required string Entity { get; init; }

    /// <summary>Concise human-readable summary of what was discovered.</summary>
    public required string Finding { get; init; }

    /// <summary>Session ID of the session in which this finding was recorded.</summary>
    public required string Source { get; init; }

    /// <summary>Estimated confidence (0–1).</summary>
    public float Confidence { get; init; } = 0.7f;

    /// <summary>Name of the agent that produced this finding.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Finding kind. Valid values: <c>observation</c>, <c>ownership</c>,
    /// <c>architectural_decision</c>, <c>dependency</c>, <c>pitfall</c>, <c>change</c>.
    /// </summary>
    public string Kind { get; init; } = "observation";

    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}
