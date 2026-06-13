namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Configuration for the adversarial orchestration mode (Selection.Type: "adversarial").
///
/// <para>
/// Applies GAN-style generate → critique → revise loops to multi-agent pipelines.
/// Each <see cref="AdversarialStageConfig"/> pairs a generator agent with a critic agent.
/// The critic always receives a fresh, isolated context window — it sees only its own system
/// instructions and the artifact to review, never the generator's reasoning history.
/// </para>
///
/// <para>
/// Stages run sequentially. The final artifact from each stage is appended to a shared
/// history that subsequent generators can build on, but critics never see this history.
/// </para>
///
/// Example YAML:
/// <code>
/// Selection:
///   Type: adversarial
///   Adversarial:
///     Rounds: 3          # 3 critiques; generator gets 2 revision opportunities
///     PassKeyword: "APPROVED"
///     Stages:
///       - Generator: Planner
///         Critic: PlanReviewer
///         Label: Planning
///       - Generator: Developer
///         Critic: CodeReviewer
///         Label: Implementation
/// </code>
/// </summary>
public record AdversarialConfig
{
    /// <summary>
    /// Ordered list of generator/critic stage pairs. Stages execute sequentially; the
    /// approved artifact from each stage is passed as context to the next generator.
    /// </summary>
    public List<AdversarialStageConfig> Stages { get; init; } = [];

    /// <summary>
    /// Maximum number of critique rounds per stage. The critic runs up to this many times;
    /// the generator gets up to <c>Rounds - 1</c> revision opportunities (the final critique
    /// round never triggers a revision, so the last reviewed artifact is always what gets
    /// promoted). Must be at least 1. Defaults to 3.
    /// </summary>
    public int Rounds { get; init; } = 3;

    /// <summary>
    /// Keyword the critic must emit on its own line (case-insensitive) to signal that the
    /// current artifact has passed review. When found the stage exits early and the artifact
    /// is promoted to the next stage without consuming remaining rounds. Defaults to "APPROVED".
    /// </summary>
    public string PassKeyword { get; init; } = "APPROVED";
}

/// <summary>A single generator/critic pair within an adversarial pipeline.</summary>
public record AdversarialStageConfig
{
    /// <summary>
    /// Name of the agent that produces and revises the artifact. Must match a name in
    /// <c>Orchestration.Agents</c>.
    /// </summary>
    public string Generator { get; init; } = string.Empty;

    /// <summary>
    /// Name of the agent that reviews the artifact with a fresh context window. Must match
    /// a name in <c>Orchestration.Agents</c>.
    /// </summary>
    public string Critic { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable label for this stage. Used in event logs and message tags.
    /// Defaults to "{Generator} → {Critic}" when null.
    /// </summary>
    public string? Label { get; init; }
}
