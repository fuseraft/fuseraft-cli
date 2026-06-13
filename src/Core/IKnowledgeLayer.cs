using fuseraft.Core.Models;

namespace fuseraft.Core;

/// <summary>
/// Unified interface to the knowledge layer.
///
/// <para>
/// All orchestrators share a single <see cref="IKnowledgeLayer"/> instance threaded through
/// <c>OrchestratorBuilder</c>. Subsystems (ADR, Graph, Memory, Provenance, Objectives) interact
/// with <em>each other</em> through this interface — they must not reference each other's concrete
/// types directly.
/// </para>
///
/// <para>
/// Subsystems are added incrementally across gaps:
/// <list type="bullet">
///   <item>Gap 1 — Architecture Decision Registry: <see cref="RecordDecisionAsync"/>, <see cref="SearchAsync"/> (decisions), <see cref="RetrieveAsync"/> (decisions)</item>
///   <item>Gap 2 — Repository Semantic Graph: <see cref="SearchAsync"/> (graph nodes), <see cref="RetrieveAsync"/> (graph nodes)</item>
///   <item>Gap 3 — Provenance: <see cref="RecordClaimAsync"/></item>
///   <item>Gap 7 — Objectives: <see cref="RecordObjectiveAsync"/></item>
/// </list>
/// </para>
/// </summary>
public interface IKnowledgeLayer
{
    /// <summary>
    /// Searches across all registered knowledge subsystems. Results are ordered by relevance.
    /// Pass <paramref name="kinds"/> to restrict to specific artifact types (e.g. only decisions).
    /// </summary>
    Task<IEnumerable<KnowledgeResult>> SearchAsync(
        string query,
        IReadOnlyList<KnowledgeKind>? kinds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a full artifact by its stable ID (e.g. <c>adr:ADR-0042</c>, <c>type:My.Ns.Foo</c>).
    /// Returns <c>null</c> when no artifact matches.
    /// </summary>
    Task<KnowledgeArtifact?> RetrieveAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Records a verifiable claim with supporting evidence. Confidence tier is computed
    /// automatically from the <paramref name="support"/> composition by
    /// <see cref="fuseraft.Infrastructure.Chat.ConfidenceComputer"/>.
    /// </summary>
    Task<ClaimRecord> RecordClaimAsync(
        string claim,
        IReadOnlyList<EvidenceClass> support,
        string? artifactId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Persists an architecture decision record and wires its graph node and <c>adr_governs</c>
    /// edges so the decision is reachable via graph traversal.
    /// </summary>
    Task<AdrEntry> RecordDecisionAsync(AdrEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Records a long-horizon objective.
    /// Implemented in Gap 7 (Long-Horizon Objective Tracking).
    /// </summary>
    Task<Objective> RecordObjectiveAsync(Objective objective, CancellationToken ct = default);
}
