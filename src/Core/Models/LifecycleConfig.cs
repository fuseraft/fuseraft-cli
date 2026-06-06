namespace fuseraft.Core.Models;

/// <summary>
/// Configures how each knowledge artifact type ages, decays, and is pruned.
/// Loaded from <c>.fuseraft/knowledge/lifecycle.yaml</c>; defaults apply when the file is absent.
/// </summary>
public sealed record LifecyclePolicy
{
    /// <summary>
    /// Archive superseded ADRs after they have been in Superseded status for at least this many days.
    /// 0 = archive immediately on the next gc run (any superseded ADR is eligible).
    /// Default: 0 (archive all superseded ADRs).
    /// </summary>
    public int AdrRetentionDays { get; init; } = 0;

    /// <summary>
    /// Demote Approved repository memories to Candidate when they have not been reinforced
    /// for at least this many days. Default: 90 days.
    /// </summary>
    public int MemoryReinforceWindowDays { get; init; } = 90;

    /// <summary>
    /// Downgrade Verified provenance claims to Inferred when the claim has no explicit
    /// <c>ExpiresAt</c> and its <c>VerifiedAt</c> is older than this many days.
    /// 0 = disable decay. Default: 30 days.
    /// </summary>
    public int ConfidenceDecayDays { get; init; } = 30;

    /// <summary>
    /// Remove graph nodes with no edges and no recent file touch after this many days.
    /// 0 = disable orphan pruning. Default: 7 days.
    /// </summary>
    public int OrphanedNodeGracePeriodDays { get; init; } = 7;

    /// <summary>
    /// Archive provenance records whose <c>ExpiresAt</c> has passed.
    /// Records without <c>ExpiresAt</c> are governed by <see cref="ConfidenceDecayDays"/>.
    /// Default: archive all expired records (any record past ExpiresAt is eligible).
    /// </summary>
    public int MaxProvenanceAgeDays { get; init; } = 0;

    /// <summary>
    /// Delete Candidate repository memories whose <c>LastReinforcedAt</c> is older than
    /// this many days. Candidate entries that never gain enough evidence to be Approved
    /// are pruned once they exceed this window. 0 = disable pruning. Default: 180 days.
    /// </summary>
    public int MemoryCandidatePruningDays { get; init; } = 180;
}

/// <summary>
/// Report returned by <see cref="fuseraft.Infrastructure.KnowledgeLifecycleManager.RunAsync"/>.
/// Describes what was archived, demoted, decayed, or pruned.
/// </summary>
public sealed record GcReport
{
    public IReadOnlyList<string> ArchivedDecisionIds   { get; init; } = [];
    public IReadOnlyList<string> DemotedMemoryIds      { get; init; } = [];
    public IReadOnlyList<string> PrunedMemoryIds       { get; init; } = [];
    public IReadOnlyList<string> DecayedClaimIds       { get; init; } = [];
    public IReadOnlyList<string> PrunedNodeIds         { get; init; } = [];
    public IReadOnlyList<string> ArchivedProvenanceIds { get; init; } = [];

    public bool IsEmpty =>
        ArchivedDecisionIds.Count   == 0 &&
        DemotedMemoryIds.Count      == 0 &&
        PrunedMemoryIds.Count       == 0 &&
        DecayedClaimIds.Count       == 0 &&
        PrunedNodeIds.Count         == 0 &&
        ArchivedProvenanceIds.Count == 0;
}
