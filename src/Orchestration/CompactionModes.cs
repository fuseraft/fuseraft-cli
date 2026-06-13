namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for the conversation compaction modes used in config.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class CompactionModes
{
    public const string Llm      = "llm";
    public const string Window   = "window";
    public const string Intent   = "intent";
    public const string Lossless = "lossless";
    public const string Hybrid   = "hybrid";
}
