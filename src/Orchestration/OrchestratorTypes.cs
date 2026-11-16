namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for the orchestrator/selection strategy types used in config.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class OrchestratorTypes
{
    public const string Magentic       = "magentic";
    public const string StateMachine   = "statemachine";
    public const string Adversarial    = "adversarial";
    public const string MapReduce      = "mapreduce";
    public const string ScatterGather  = "scattergather";
}
