namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for all orchestration event types written to events.jsonl.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class EventTypes
{
    public const string TurnStart        = "turn_start";
    public const string TurnEnd          = "turn_end";
    public const string SessionStart     = "session_start";
    public const string SessionError     = "session_error";
    public const string ToolCall         = "tool_call";
    public const string ToolBlocked      = "tool_blocked";
    public const string ValidationFail   = "validation_fail";
    public const string HitlEscalation   = "hitl_escalation";
    public const string ContextAssembly  = "context_assembly";
    public const string InnerCallContext = "inner_call_context";
    public const string Reasoning        = "reasoning";
    public const string KeywordNotFound  = "keyword_not_found";
    public const string MagenticPlan     = "magentic_plan";
    public const string MagenticComplete = "magentic_complete";
}
