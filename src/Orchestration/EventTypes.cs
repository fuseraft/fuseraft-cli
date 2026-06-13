namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for all orchestration event types written to events.jsonl.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class EventTypes
{
    // ── Core turn / session lifecycle ────────────────────────────────────────
    public const string TurnStart        = "turn_start";
    public const string TurnEnd          = "turn_end";
    public const string TurnTimeout      = "turn_timeout";
    public const string SessionStart     = "session_start";
    public const string SessionEnd       = "session_end";
    public const string SessionError     = "session_error";
    public const string SessionSummary   = "session_summary";

    // ── Agent routing / state machine ────────────────────────────────────────
    public const string AgentRouted          = "agent_routed";
    public const string AgentBlocked         = "agent_blocked";
    public const string StateAdvanced        = "state_advanced";
    public const string KeywordDetected      = "keyword_detected";
    public const string KeywordNotFound      = "keyword_not_found";
    public const string MultiKeyword         = "multi_keyword";
    public const string NoKeyword            = "no_keyword";
    public const string BackEdgeEscalation   = "back_edge_escalation";
    public const string ReplanBlocked        = "replan_blocked";

    // ── Parallel / phase execution ───────────────────────────────────────────
    public const string PhaseStart     = "phase_start";
    public const string PhaseEnd       = "phase_end";
    public const string ParallelStart  = "parallel_start";
    public const string ParallelMerge  = "parallel_merge";

    // ── Tool use ─────────────────────────────────────────────────────────────
    public const string ToolCall    = "tool_call";
    public const string ToolBlocked = "tool_blocked";

    // ── Validation / governance ──────────────────────────────────────────────
    public const string ValidationFail      = "validation_fail";
    public const string HitlEscalation      = "hitl_escalation";
    public const string CircuitBreakerOpen  = "circuit_breaker_open";
    public const string RecoveryActivated   = "recovery_activated";

    // ── Context / token budget ───────────────────────────────────────────────
    public const string ContextAssembly        = "context_assembly";
    public const string InnerCallContext        = "inner_call_context";
    public const string ContextBudgetWarn       = "context_budget_warn";
    public const string ContextBudgetCutover    = "context_budget_cutover";
    public const string ContextCapWarning       = "context_cap_warning";
    public const string ContextExceededRecovery = "context_exceeded_recovery";
    public const string ContextWarning          = "context_warning";

    // ── Compaction ───────────────────────────────────────────────────────────
    public const string Compaction               = "compaction";
    public const string CompactionResumeCandidate = "compaction_resume_candidate";

    // ── Correction / plan ────────────────────────────────────────────────────
    public const string CorrectionInjected = "correction_injected";
    public const string PlanCaptured       = "plan_captured";
    public const string StepComplete       = "step_complete";
    public const string StepHalted         = "step_halted";

    // ── Skill curation ───────────────────────────────────────────────────────
    public const string SkillCurationStart    = "skill_curation_start";
    public const string SkillCurationComplete = "skill_curation_complete";

    // ── Sub-agent ────────────────────────────────────────────────────────────
    public const string SubAgentStart   = "sub_agent_start";
    public const string SubAgentEnd     = "sub_agent_end";
    public const string SubAgentToolCall = "sub_agent_tool_call";

    // ── Magentic orchestrator ────────────────────────────────────────────────
    public const string MagenticPlan     = "magentic_plan";
    public const string MagenticComplete = "magentic_complete";
    public const string MagenticReplan   = "magentic_replan";

    // ── Saga orchestrator ────────────────────────────────────────────────────
    public const string SagaCompensating = "saga_compensating";
    public const string SagaCompensated  = "saga_compensated";

    // ── Adversarial orchestrator ─────────────────────────────────────────────
    public const string AdversarialStageStart   = "adversarial_stage_start";
    public const string AdversarialStagePass    = "adversarial_stage_pass";
    public const string AdversarialStageTimeout = "adversarial_stage_timeout";
    public const string AdversarialComplete     = "adversarial_complete";

    // ── Reasoning / HTTP ─────────────────────────────────────────────────────
    public const string Reasoning    = "reasoning";
    public const string HttpReasoning = "http_reasoning";

    // ── REPL ─────────────────────────────────────────────────────────────────
    public const string UserInput        = "user_input";
    public const string AssistantResponse = "assistant_response";
    public const string Command          = "command";
    public const string Cancelled        = "cancelled";
    public const string ReplError        = "repl_error";
    public const string ReplWarning      = "repl_warning";
}
