namespace fuseraft.Core.Events;

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
    public const string SessionRecovered = "session_recovered";
    public const string SessionAborted   = "session_aborted";

    // ── Agent execution lifecycle ─────────────────────────────────────────────
    public const string AgentStart   = "agent_start";
    public const string AgentEnd     = "agent_end";
    public const string AgentError   = "agent_error";
    public const string AgentTimeout = "agent_timeout";

    // ── Agent routing / state machine ────────────────────────────────────────
    public const string AgentRouted          = "agent_routed";
    public const string AgentBlocked         = "agent_blocked";
    public const string StateAdvanced        = "state_advanced";
    public const string KeywordDetected      = "keyword_detected";
    public const string KeywordNotFound      = "keyword_not_found";
    public const string MultiKeyword         = "multi_keyword";
    public const string BackEdgeEscalation   = "back_edge_escalation";
    public const string ReplanBlocked        = "replan_blocked";

    // ── Parallel / phase execution ───────────────────────────────────────────
    public const string PhaseStart           = "phase_start";
    public const string PhaseEnd             = "phase_end";
    public const string ParallelStart        = "parallel_start";
    public const string ParallelMerge        = "parallel_merge";
    public const string ParallelBranchStart  = "parallel_branch_start";
    public const string ParallelBranchEnd    = "parallel_branch_end";
    public const string ParallelBranchError  = "parallel_branch_error";

    // ── Tool use ─────────────────────────────────────────────────────────────
    public const string ToolCall    = "tool_call";
    public const string ToolBlocked = "tool_blocked";
    public const string ToolResult  = "tool_result";
    public const string ToolError   = "tool_error";
    public const string ToolTimeout = "tool_timeout";

    // ── Validation / governance ──────────────────────────────────────────────
    public const string ValidationFail       = "validation_fail";
    public const string HitlEscalation       = "hitl_escalation";
    public const string HitlApproved         = "hitl_approved";
    public const string HitlRejected         = "hitl_rejected";
    public const string HitlResolved         = "hitl_resolved";
    public const string CircuitBreakerOpen   = "circuit_breaker_open";
    public const string RecoveryActivated    = "recovery_activated";
    public const string RetryScheduled       = "retry_scheduled";
    public const string RetryAttempt         = "retry_attempt";
    public const string RetryExhausted       = "retry_exhausted";
    public const string TerminationSatisfied = "termination_satisfied";
    public const string TerminationForced    = "termination_forced";
    public const string MaxTurnsExceeded     = "max_turns_exceeded";

    // ── Context / token budget ───────────────────────────────────────────────
    public const string ContextAssembly         = "context_assembly";
    public const string InnerCallContext        = "inner_call_context";
    public const string ContextBudgetWarn       = "context_budget_warn";
    public const string ContextBudgetCutover    = "context_budget_cutover";
    public const string ContextWindowWarn       = "context_window_warn";
    public const string ContextExceededRecovery = "context_exceeded_recovery";
    public const string ContextWarning          = "context_warning";

    // ── Compaction ───────────────────────────────────────────────────────────
    public const string Compaction                = "compaction";
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
    public const string SubAgentStart    = "sub_agent_start";
    public const string SubAgentEnd      = "sub_agent_end";
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

    // ── Model invocation ─────────────────────────────────────────────────────
    public const string ModelCall     = "model_call";
    public const string ModelResponse = "model_response";
    public const string ModelError    = "model_error";
    public const string ModelTimeout  = "model_timeout";

    // ── Reasoning / HTTP ─────────────────────────────────────────────────────
    public const string Reasoning     = "reasoning";
    public const string HttpReasoning = "http_reasoning";

    // ── Selection strategy ───────────────────────────────────────────────────
    public const string SelectionEvaluated = "selection_evaluated";
    public const string SelectionFallback  = "selection_fallback";

    // ── Knowledge retrieval ──────────────────────────────────────────────────
    public const string KnowledgeLookup = "knowledge_lookup";
    public const string KnowledgeHit    = "knowledge_hit";
    public const string KnowledgeMiss   = "knowledge_miss";

    // ── Artifact lifecycle ───────────────────────────────────────────────────
    public const string ArtifactCreated = "artifact_created";
    public const string ArtifactUpdated = "artifact_updated";
    public const string ArtifactDeleted = "artifact_deleted";

    // ── Checkpointing / replay ───────────────────────────────────────────────
    public const string CheckpointCreated       = "checkpoint_created";
    public const string CheckpointLoaded        = "checkpoint_loaded";
    public const string ResumeStarted           = "resume_started";
    public const string ResumeCompleted         = "resume_completed";
    public const string EventReplayStart        = "event_replay_start";
    public const string EventReplayComplete     = "event_replay_complete";
    public const string EventCorruptionDetected = "event_corruption_detected";

    // ── REPL ─────────────────────────────────────────────────────────────────
    public const string UserInput             = "user_input";
    public const string AssistantResponse     = "assistant_response";
    public const string Command               = "command";
    public const string Cancelled             = "cancelled";
    public const string CancellationRequested = "cancellation_requested";
    public const string CancellationObserved  = "cancellation_observed";
    public const string ReplError             = "repl_error";
    public const string ReplWarning           = "repl_warning";
    public const string FileChanges           = "file_changes";
    public const string HistoryTrimmed        = "history_trimmed";
}
