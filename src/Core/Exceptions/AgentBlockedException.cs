namespace fuseraft.Core.Exceptions;

/// <summary>
/// Thrown when an agent emits the <c>BLOCKED</c> keyword on its own line, signalling
/// an unrecoverable blocker that cannot be resolved through retries or corrections.
/// The orchestrator catches this and halts the session immediately.
/// </summary>
public sealed class AgentBlockedException : Exception
{
    /// <summary>Name of the agent that declared the blocker.</summary>
    public string AgentName { get; }

    /// <summary>The full response text containing the BLOCKED signal and reason.</summary>
    public string BlockerMessage { get; }

    public AgentBlockedException(string agentName, string blockerMessage)
        : base($"Agent '{agentName}' declared a blocker and cannot proceed.")
    {
        AgentName      = agentName;
        BlockerMessage = blockerMessage;
    }
}
