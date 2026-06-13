namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// JSON-structured progress ledger emitted by the Magentic manager agent at each inner-loop step.
/// The manager LLM is prompted to respond with a JSON blob matching this schema so the
/// orchestrator can steer the conversation without hard-coded keyword routing.
/// </summary>
public record MagenticProgressLedger
{
    /// <summary>True when the original task is fully complete and verified.</summary>
    public bool IsRequestSatisfied { get; init; }

    /// <summary>True when the team is repeating steps without meaningful progress.</summary>
    public bool IsInLoop { get; init; }

    /// <summary>True when the last round produced meaningful forward movement.</summary>
    public bool IsProgressBeingMade { get; init; }

    /// <summary>The exact name of the next participant agent to invoke.</summary>
    public string? NextSpeaker { get; init; }

    /// <summary>A clear, specific, actionable instruction for <see cref="NextSpeaker"/>.</summary>
    public string? InstructionOrQuestion { get; init; }

    /// <summary>
    /// Populated by the manager when <see cref="IsRequestSatisfied"/> is true.
    /// Becomes the final session-ending message.
    /// </summary>
    public string? FinalAnswer { get; init; }

    /// <summary>
    /// Step numbers (1-based) that the manager considers fully complete as of this round.
    /// Used to build a structured progress checklist in the ledger prompt so subsequent
    /// evaluations know which steps have already been verified.
    /// </summary>
    public int[]? StepsCompleted { get; init; }
}
