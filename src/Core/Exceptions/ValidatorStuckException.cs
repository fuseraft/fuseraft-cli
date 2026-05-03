namespace fuseraft.Core.Exceptions;

/// <summary>
/// Thrown by <c>KeywordSelectionStrategy</c> when the same routing validator has
/// blocked the same agent on the same route for <c>ConsecutiveValidatorFailureThreshold</c>
/// consecutive turns, indicating the agent is stuck and cannot self-correct.
///
/// <para>
/// The orchestrator catches this exception and triggers human-in-the-loop intervention:
/// the user is prompted for a redirect message which is injected as a user turn so the
/// agent receives direct guidance on how to proceed.
/// </para>
/// </summary>
public sealed class ValidatorStuckException : Exception
{
    /// <summary>Name of the agent that has been stuck.</summary>
    public string AgentName { get; }

    /// <summary>Config name of the validator that kept blocking (e.g. "RequireBrief").</summary>
    public string ValidatorName { get; }

    /// <summary>How many consecutive turns the validator has failed for this agent/route.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>The error message from the last validator failure.</summary>
    public string LastValidatorError { get; }

    public ValidatorStuckException(
        string agentName,
        string validatorName,
        int consecutiveFailures,
        string lastValidatorError)
        : base(
            $"Agent '{agentName}' has been blocked by validator '{validatorName}' " +
            $"for {consecutiveFailures} consecutive turns and cannot self-correct.")
    {
        AgentName            = agentName;
        ValidatorName        = validatorName;
        ConsecutiveFailures  = consecutiveFailures;
        LastValidatorError   = lastValidatorError;
    }
}
