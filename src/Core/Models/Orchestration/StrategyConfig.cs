namespace fuseraft.Core.Models.Orchestration;

/// <summary>Which runtime drives a compiled team: the state machine for Steps flows, or a pattern.</summary>
public record SelectionStrategyConfig
{
    /// <summary><c>statemachine</c>, <c>magentic</c>, <c>adversarial</c>, <c>mapreduce</c> or <c>scattergather</c>.</summary>
    public string Type { get; init; } = "statemachine";

    /// <summary>The compiled Steps flow; set when <see cref="Type"/> is <c>statemachine</c>.</summary>
    public StateMachineConfig? StateMachine { get; init; }

    /// <summary>Manager settings for the <c>magentic</c> pattern.</summary>
    public MagenticManagerConfig? Magentic { get; init; }

    /// <summary>Stages for the <c>adversarial</c> pattern.</summary>
    public AdversarialConfig? Adversarial { get; init; }

    /// <summary>Splitter, mapper and reducer for the <c>mapreduce</c> pattern.</summary>
    public MapReduceConfig? MapReduce { get; init; }

    /// <summary>Participants and synthesizer for the <c>scattergather</c> pattern.</summary>
    public ScatterGatherConfig? ScatterGather { get; init; }
}

/// <summary>
/// Configuration for the Magentic orchestration mode.
/// The manager LLM drives a two-level loop: an outer loop that gathers facts and creates
/// a plan, and an inner loop that evaluates progress, selects the next participant, and
/// detects stalls or task completion.
/// </summary>
public record MagenticManagerConfig
{
    /// <summary>
    /// Model configuration for the manager agent LLM.
    /// A reasoning-capable model is strongly recommended.
    /// Must reference a named alias from <c>Orchestration.Models</c> or be a fully-specified
    /// <see cref="ModelConfig"/>.
    /// </summary>
    public ModelConfig? Model { get; init; }

    /// <summary>
    /// Optional system instructions for the manager agent.
    /// When null, a built-in default prompt is used.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Maximum number of inner-loop coordination rounds before the orchestration terminates.
    /// Defaults to 20.
    /// </summary>
    public int MaxRoundCount { get; init; } = 20;

    /// <summary>
    /// Number of consecutive rounds without forward progress before a replan is triggered.
    /// Defaults to 3.
    /// </summary>
    public int MaxStallCount { get; init; } = 3;

    /// <summary>
    /// Maximum number of replan cycles. After this limit the orchestration terminates
    /// with a stall message rather than looping indefinitely.
    /// Defaults to 2.
    /// </summary>
    public int MaxResetCount { get; init; } = 2;

    /// <summary>
    /// When true, the manager pauses after generating an initial plan and waits for HITL
    /// review before proceeding to the inner loop. Integrates with
    /// <see cref="IHumanApprovalService.PromptPlanReviewAsync"/>.
    /// Defaults to false.
    /// </summary>
    public bool EnablePlanReview { get; init; } = false;
}
