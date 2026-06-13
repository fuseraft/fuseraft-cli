namespace fuseraft.Cli;

// Compaction trigger classification — informs the session_summary event and the
// compaction event reason field so post-session analysis can identify the primary
// cause of each compaction cycle.
internal static class CompactionReason
{
    public const string SingleTurnLimit  = "single_turn_limit";
    public const string CumulativeBudget = "cumulative_budget";
    public const string ShouldCompact    = "window_size";
    public const string AgentRequested   = "agent_requested";
    public const string ContextExceeded  = "context_exceeded";
}
