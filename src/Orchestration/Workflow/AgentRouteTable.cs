using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Workflow;

// Route table DTOs shared by GraphOrchestrator and CorrectionEngine.

/// <summary>Per-executor routing metadata.</summary>
internal sealed class AgentRouteTable
{
    /// <summary>Send-forward routes: keyword → next executor info + validators.</summary>
    public Dictionary<string, RouteInfo> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keywords that break the current phase and trigger an outer-loop restart.</summary>
    public HashSet<string> PhaseBreakKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validators run before APPROVED is accepted (RequireShellPass + RequireReviewJudgement).</summary>
    public IReadOnlyList<IRoutingValidator> TerminalValidators { get; set; } = [];

    /// <summary>
    /// Per-keyword validators for phase-break (back-edge) keywords.
    /// Populated by <c>GraphOrchestrator.BuildNodeRouteTables</c> when a back-edge declares
    /// validators. All validators for the keyword must pass before the phase-break fires.
    /// </summary>
    public Dictionary<string, IReadOnlyList<IRoutingValidator>> PhaseBreakValidators { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Back-edge keywords that require human approval before the phase-break fires.
    /// Populated by <c>GraphOrchestrator.BuildNodeRouteTables</c>.
    /// </summary>
    public HashSet<string> PhaseBreakRequireHumanApproval { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-keyword recovery agent names for back-edge validator failures.
    /// When a back-edge validator fails consecutively, the named agent is invoked for one
    /// intervention turn before control returns to the source agent.
    /// </summary>
    public Dictionary<string, string> PhaseBreakRecoveryAgents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Send-forward keywords that belong to OTHER agents' route tables.
    /// Populated by <c>GraphOrchestrator.BuildNodeRouteTables</c> so that
    /// <see cref="CorrectionEngine.InjectNoKeywordCorrection"/> can produce a specific
    /// "wrong keyword" error instead of a generic "no keyword" correction when an agent
    /// emits a keyword that belongs to a different node.
    /// </summary>
    public HashSet<string> ForeignSendForwardKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keywords that trigger a parallel fan-out from this node.
    /// These do NOT appear in <see cref="Routes"/> — they are handled by the
    /// fan-out mechanism in <c>GraphOrchestrator</c>. Included here so that
    /// <see cref="CorrectionEngine.BuildValidKeywordList"/> and
    /// <see cref="KeywordDetector.DetectKeywords"/> surface them to agents.
    /// </summary>
    public HashSet<string> ParallelKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Information about a single send-forward route.</summary>
internal sealed record RouteInfo(
    string NextExecutorId,
    string NextExecutorName,
    IReadOnlyList<IRoutingValidator> Validators,
    bool RequireHumanApproval = false,
    string? RecoveryAgent = null);
