namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for the reserved AgentName values used in AgentMessage.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class AgentNames
{
    public const string System       = "System";
    public const string Orchestrator = "Orchestrator";
    public const string Human        = "Human";
    public const string Assistant    = "Assistant";
    public const string Verifier     = "Verifier";
    public const string Unknown      = "Unknown";
}
