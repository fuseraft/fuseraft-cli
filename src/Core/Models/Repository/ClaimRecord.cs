namespace fuseraft.Core.Models.Repository;

/// <summary>
/// A verifiable claim with supporting evidence, computed confidence tier, and optional expiry.
///
/// <para>
/// <c>Status</c> is never caller-supplied: it is always computed by
/// <see cref="fuseraft.Infrastructure.ConfidenceComputer.Compute"/> from the <see cref="Support"/>
/// composition.  Callers set <see cref="ExpiresAt"/> based on the volatility of the claim —
/// a build-pass claim expires quickly; an ADR-backed architectural claim may never expire.
/// </para>
/// </summary>
public sealed record ClaimRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The claim being made, in plain language.</summary>
    public string Claim { get; init; } = string.Empty;

    /// <summary>The artifact or evidence-graph node this claim is about.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Evidence classes backing this claim. Determines <see cref="Status"/> via ConfidenceComputer.</summary>
    public List<EvidenceClass> Support { get; init; } = [];

    /// <summary>Computed confidence tier: Verified / Inferred / Assumed / Guessed.</summary>
    public string Status { get; init; } = "Guessed";

    /// <summary>Artifact IDs or node IDs that constitute the supporting evidence.</summary>
    public List<string> ProvenanceSources { get; init; } = [];

    /// <summary>When this claim was first recorded.</summary>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When supporting evidence was collected. Null until the claim is verified.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>
    /// When this verification is no longer trusted. Null means the claim does not expire.
    /// Callers set this based on claim volatility (e.g. a build-pass claim expires in hours;
    /// an ADR-backed architectural claim may be indefinite).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
