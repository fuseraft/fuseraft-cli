namespace fuseraft.Core.Models.Repository;

/// <summary>
/// A durable, cross-session pattern extracted from observable evidence.
///
/// <para>
/// Entries start as <c>Candidate</c> after extraction and become <c>Approved</c>
/// only through human review (<c>fuseraft memory review</c>) or an automated
/// reviewer agent. Candidates are never injected into agent prompts.
/// When an approved pattern recurs across sessions, <see cref="ReinforcementCount"/>
/// is incremented and <see cref="Confidence"/> is recomputed by
/// <see cref="fuseraft.Infrastructure.Chat.ConfidenceComputer"/>.
/// </para>
/// </summary>
public sealed record RepositoryMemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The recurring pattern or fact observed across sessions.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Computed confidence tier (Verified / Inferred / Assumed / Guessed).</summary>
    public string Confidence { get; init; } = "Guessed";

    /// <summary>Evidence classes backing this entry — drives the confidence computation.</summary>
    public List<EvidenceClass> Evidence { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastReinforcedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>How many sessions have independently produced the same pattern.</summary>
    public int ReinforcementCount { get; init; }

    /// <summary>Lifecycle state: Candidate, Approved, or Rejected.</summary>
    public string Status { get; init; } = "Candidate";

    /// <summary>Session ID that first produced this entry.</summary>
    public string? SourceSessionId { get; init; }
}
