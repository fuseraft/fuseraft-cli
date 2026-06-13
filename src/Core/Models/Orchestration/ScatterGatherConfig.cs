namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Configuration for the scatter-gather orchestration mode (Selection.Type: "scattergather").
///
/// <para>
/// <b>Phase 1 — Scatter</b>: every agent listed in <see cref="Participants"/> receives the
/// same task in parallel. Each participant runs in an isolated history snapshot — they cannot
/// see each other's in-progress work. This produces N independent responses from N different
/// agents (or N invocations of the same agent).
/// </para>
///
/// <para>
/// <b>Phase 2 — Gather</b>: the <see cref="Synthesizer"/> agent receives the original task
/// history plus every participant's labeled output, then produces a single final answer.
/// The synthesizer may vote, merge, rank, reconcile, or summarise — depending on how it is
/// instructed.
/// </para>
///
/// Example YAML:
/// <code>
/// Selection:
///   Type: scattergather
///   ScatterGather:
///     Participants:
///       - LegalReviewer
///       - TechnicalReviewer
///       - BusinessReviewer
///     Synthesizer: LeadReviewer
///     MaxConcurrency: 0          # 0 = unlimited; all participants run concurrently
/// </code>
/// </summary>
public record ScatterGatherConfig
{
    /// <summary>
    /// Names of the agents to invoke in parallel, each receiving the same task.
    /// Every name must match an agent declared in <c>Orchestration.Agents</c>.
    /// At least one participant is required.
    /// </summary>
    public List<string> Participants { get; init; } = [];

    /// <summary>
    /// Name of the agent that synthesises all participant outputs into a final answer.
    /// Must match a name in <c>Orchestration.Agents</c>.
    /// </summary>
    public string Synthesizer { get; init; } = string.Empty;

    /// <summary>
    /// Maximum number of participant agents to run concurrently. 0 means all participants
    /// run simultaneously (unbounded parallelism). Defaults to 0.
    /// </summary>
    public int MaxConcurrency { get; init; } = 0;
}
