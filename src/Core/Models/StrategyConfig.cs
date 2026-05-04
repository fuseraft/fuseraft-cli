namespace fuseraft.Core.Models;

/// <summary>
/// Configures which agent selection strategy the orchestrator uses.
/// </summary>
public record SelectionStrategyConfig
{
    /// <summary>
    /// Strategy type.
    /// <list type="bullet">
    ///   <item><c>sequential</c>: round-robin through agents in order (default).</item>
    ///   <item><c>llm</c>: an LLM call picks the next agent each turn.</item>
    ///   <item><c>keyword</c>: deterministic routing based on keywords in the last message.</item>
    ///   <item><c>structured</c>: routing based on JSON field conditions in the last message.</item>
    /// </list>
    /// </summary>
    public string Type { get; init; } = "sequential";

    /// <summary>
    /// Prompt template used by the <c>llm</c> strategy.
    /// Use <c>{{$agents}}</c> and <c>{{$history}}</c> as placeholders.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Model config for the LLM-based selection strategy.
    /// </summary>
    public ModelConfig? Model { get; init; }

    /// <summary>
    /// Routing rules for the <c>keyword</c> strategy. Evaluated in order; first match wins.
    /// </summary>
    public List<KeywordRoute>? Routes { get; init; }

    /// <summary>
    /// Routing rules for the <c>structured</c> strategy. Evaluated in order; first match wins.
    /// Each route fires when the last agent message is valid JSON and the declared
    /// <see cref="StructuredRoute.Condition"/> evaluates to true against the parsed object.
    /// </summary>
    public List<StructuredRoute>? StructuredRoutes { get; init; }

    /// <summary>
    /// Default agent name for the <c>keyword</c> and <c>structured</c> strategies when no
    /// route matches. Defaults to the first agent in the config.
    /// </summary>
    public string? DefaultAgent { get; init; }

    /// <summary>
    /// Manager agent configuration for the <c>magentic</c> strategy.
    /// Required when <see cref="Type"/> is <c>"magentic"</c>.
    /// </summary>
    public MagenticManagerConfig? Magentic { get; init; }

    /// <summary>
    /// Explicit state graph configuration for the <c>statemachine</c> strategy.
    /// Required when <see cref="Type"/> is <c>"statemachine"</c>.
    ///
    /// <para>
    /// The state machine tracks the current state explicitly, matches agent signals
    /// against the current state's outgoing transitions, and requires all declared
    /// contracts to pass before firing a transition. Agents do not control flow —
    /// they emit signals; the system resolves transitions.
    /// </para>
    /// </summary>
    public StateMachineConfig? StateMachine { get; init; }

    /// <summary>
    /// Declarative directed-graph configuration for the <c>graph</c> selection type.
    /// Required when <see cref="Type"/> is <c>"graph"</c>.
    ///
    /// <para>
    /// Agents are bound to named nodes; directed edges with optional keyword conditions
    /// and routing validators define control flow. Forward edges are wired into a MAF
    /// <c>WorkflowBuilder</c> phase; back-edges restart the outer phase loop from the
    /// target node, enabling cycles without violating the MAF DAG constraint per phase.
    /// Terminal nodes end the session after the agent executes once.
    /// </para>
    /// </summary>
    public GraphConfig? Graph { get; init; }
}

/// <summary>
/// Configuration for the Magentic orchestration mode.
/// The manager LLM drives a two-level loop: an outer loop that gathers facts and creates
/// a plan, and an inner loop that evaluates progress, selects the next participant, and
/// detects stalls or task completion.
/// </summary>
public record MagenticManagerConfig
{
    /// <summary>
    /// Model configuration for the manager agent LLM.
    /// A reasoning-capable model is strongly recommended.
    /// Must reference a named alias from <c>Orchestration.Models</c> or be a fully-specified
    /// <see cref="ModelConfig"/>.
    /// </summary>
    public ModelConfig? Model { get; init; }

    /// <summary>
    /// Optional system instructions for the manager agent.
    /// When null, a built-in default prompt is used.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Maximum number of inner-loop coordination rounds before the orchestration terminates.
    /// Defaults to 20.
    /// </summary>
    public int MaxRoundCount { get; init; } = 20;

    /// <summary>
    /// Number of consecutive rounds without forward progress before a replan is triggered.
    /// Defaults to 3.
    /// </summary>
    public int MaxStallCount { get; init; } = 3;

    /// <summary>
    /// Maximum number of replan cycles. After this limit the orchestration terminates
    /// with a stall message rather than looping indefinitely.
    /// Defaults to 2.
    /// </summary>
    public int MaxResetCount { get; init; } = 2;

    /// <summary>
    /// When true, the manager pauses after generating an initial plan and waits for HITL
    /// review before proceeding to the inner loop. Integrates with
    /// <see cref="IHumanApprovalService.PromptPlanReviewAsync"/>.
    /// Defaults to false.
    /// </summary>
    public bool EnablePlanReview { get; init; } = false;
}

// ---------------------------------------------------------------------------
// Structured routing

/// <summary>A single condition → agent routing rule for the <c>structured</c> strategy.</summary>
public record StructuredRoute
{
    /// <summary>Agent to activate when <see cref="Condition"/> evaluates to true.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>
    /// Condition evaluated against the parsed JSON of the last agent message.
    /// The route fires when the condition is true.
    /// </summary>
    public StructuredCondition Condition { get; init; } = new();

    /// <summary>
    /// Optional list of agent names that are permitted to trigger this route.
    /// When set, the route only fires if the current message author is in this list.
    /// </summary>
    public List<string>? SourceAgents { get; init; }
}

/// <summary>
/// A predicate evaluated against a parsed JSON object.
/// Exactly one of <see cref="Equals"/>, <see cref="NotEquals"/>, <see cref="Contains"/>,
/// or <see cref="Exists"/> should be set.
/// </summary>
public record StructuredCondition
{
    /// <summary>
    /// Dot-separated path to the JSON field to evaluate.
    /// Example: <c>"review_result"</c> or <c>"data.status"</c>.
    /// </summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>The field value must equal this string (case-insensitive).</summary>
    public string? Is { get; init; }

    /// <summary>The field value must NOT equal this string (case-insensitive).</summary>
    public string? IsNot { get; init; }

    /// <summary>The field value must contain this substring (case-insensitive).</summary>
    public string? Contains { get; init; }

    /// <summary>
    /// When <c>true</c>, the field must be present and non-null.
    /// When <c>false</c>, the field must be absent or null.
    /// </summary>
    public bool? Exists { get; init; }
}

/// <summary>A single keyword → agent routing rule.</summary>
public record KeywordRoute
{
    /// <summary>Substring to search for in the last message (case-insensitive).</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>Agent name to route to when <see cref="Keyword"/> is found.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>
    /// Optional validator name to run before this route fires.
    /// Built-in validators: <c>"RequireWriteFile"</c> (blocks HANDOFF TO TESTER unless
    /// the agent called <c>write_file</c> this turn), <c>"RequireShellPass"</c> (blocks
    /// unless a shell command exited 0 this turn), <c>"RequireBrief"</c> (blocks
    /// HANDOFF TO DEVELOPER unless <c>brief.json</c> exists with valid content),
    /// <c>"TestReportValid"</c> (blocks HANDOFF TO REVIEWER unless <c>test-report.json</c>
    /// is structurally sound), and <c>"RequireRelatedTestsPass"</c> (runs incremental tests
    /// scoped to changed files using <c>TestSelector.FindRelatedCommand</c> — requires
    /// <c>TestSelector</c> to be configured at the orchestration level).
    /// When null or omitted (and <see cref="Validators"/> is also empty) no validation is
    /// performed for this route.
    /// </summary>
    public string? Validator { get; init; }

    /// <summary>
    /// Optional list of validator names that ALL must pass before this route fires (AND
    /// semantics). When non-empty this takes precedence over the single <see cref="Validator"/>
    /// field. Use this instead of defining multiple routes with the same keyword — two routes
    /// sharing a keyword are OR'd (first-match wins), not AND'd.
    ///
    /// Example — require both a file write and a passing build:
    /// <code>
    /// "Validators": ["RequireWriteFile", "RequireShellPass"]
    /// </code>
    /// </summary>
    public List<string>? Validators { get; init; }

    /// <summary>
    /// Optional list of agent names that are permitted to trigger this route.
    /// When set, the route only fires if the current agent (the one whose message
    /// contained the keyword) is in this list.  Use to prevent agents from triggering
    /// routes intended for other roles — e.g. set <c>["Planner"]</c> on
    /// <c>HANDOFF TO DEVELOPER</c> so only the Planner can initiate that transition.
    /// When null or empty, any agent may trigger the route (original behaviour).
    /// </summary>
    public List<string>? SourceAgents { get; init; }

    /// <summary>
    /// When set alongside <c>Validator = "RequireShellPass"</c>, the passing shell
    /// command must contain at least one of these pipe-separated substrings
    /// (case-insensitive).  Example: <c>"go build|go test"</c> requires that the
    /// command run was a build or test command, not an unrelated utility such as
    /// <c>go mod tidy</c>.  Ignored when <c>Validator</c> is not <c>RequireShellPass</c>.
    /// </summary>
    public string? RequiredCommandPattern { get; init; }

    /// <summary>
    /// When set alongside <c>Validator = "RequireWriteFile"</c>, a successful
    /// <c>shell_run</c> whose command matches at least one of these pipe-separated
    /// substrings is accepted as an alternative to <c>write_file</c>. Use for commands
    /// that write files through the shell rather than through the plugin — e.g.
    /// <c>"go mod tidy|go get"</c> for Go projects or <c>"npm install"</c> for Node.
    /// Ignored when <c>Validator</c> is not <c>RequireWriteFile</c>.
    /// </summary>
    public string? ShellFallbackPattern { get; init; }

    /// <summary>
    /// When true, the operator must explicitly approve (y) this route before it fires.
    /// If rejected, the source agent is re-invoked with a "route blocked" message so it
    /// can continue working or await further instructions.
    /// </summary>
    public bool RequireHumanApproval { get; init; }

    /// <summary>
    /// Optional JSON condition evaluated against the agent's response after the keyword matches.
    /// When set, the route only fires if the message contains a JSON object where this condition
    /// is true. Use this to disambiguate multiple routes sharing the same keyword by the content
    /// of the agent's structured output — e.g. route to Publisher when
    /// <c>{ "Field": "result", "Is": "approved" }</c> and to Writer when
    /// <c>{ "Field": "result", "IsNot": "approved" }</c>.
    /// When null (the default), no JSON condition is checked and the keyword alone triggers the route.
    /// </summary>
    public StructuredCondition? Condition { get; init; }

    /// <summary>
    /// When true, this route expects the agent to respond with a JSON object rather than
    /// plain text containing the keyword. The JSON condition is evaluated as the primary
    /// routing signal; the keyword serves as a fallback when structured output is absent.
    ///
    /// <para>
    /// If the agent's response cannot be parsed as JSON and the keyword is not present,
    /// a correction message is injected (up to <c>MaxStructuredParseRetries</c> times)
    /// asking the agent to re-emit a valid JSON object. Once retries are exhausted,
    /// the strategy falls back to keyword matching.
    /// </para>
    ///
    /// <para>
    /// Requires <see cref="Condition"/> to be set — <c>PreferStructuredOutput</c> without
    /// a condition has no effect.
    /// </para>
    ///
    /// Example — route to Reviewer when the agent emits <c>{"review_result": "approved"}</c>:
    /// <code>
    /// "PreferStructuredOutput": true,
    /// "Condition": { "Field": "review_result", "Is": "approved" }
    /// </code>
    /// </summary>
    public bool PreferStructuredOutput { get; init; }

    /// <summary>
    /// Optional agent to invoke when a routing validator has failed two or more consecutive
    /// times on this route. When set, the recovery agent is called in for one intervention
    /// turn (with a diagnostic message explaining what failed) before control returns to
    /// the normal pipeline. If the recovery agent resolves the blocking issue, the route
    /// fires normally on the next attempt. If not, the failure count continues toward
    /// <see cref="Core.Exceptions.ValidatorStuckException"/> and HITL escalation.
    ///
    /// <para>
    /// Recovery activates at most once per agent/route pair per session to prevent
    /// infinite recovery loops.
    /// </para>
    ///
    /// Example — a Senior Developer steps in if the Developer fails validation twice:
    /// <code>
    /// "RecoveryAgent": "SeniorDeveloper"
    /// </code>
    /// </summary>
    public string? RecoveryAgent { get; init; }

    /// <summary>
    /// Names of evidence contracts that must all be satisfied before this route fires.
    /// Contracts are declared under <c>Orchestration.Contracts</c> and referenced here
    /// by name. They run with AND semantics alongside any <see cref="Validators"/> entries.
    ///
    /// Example — require the ImplementationComplete contract before handing off to Tester:
    /// <code>
    /// Contracts: [ImplementationComplete]
    /// </code>
    /// </summary>
    public List<string>? Contracts { get; init; }
}

/// <summary>
/// Configures when the orchestrator stops the conversation.
/// </summary>
public record TerminationStrategyConfig
{
    /// <summary>
    /// Strategy type.
    /// <list type="bullet">
    ///   <item><c>regex</c>: stop when a message matches a regex pattern.</item>
    ///   <item><c>maxiterations</c>: stop after N turns regardless.</item>
    ///   <item><c>composite</c>: stop when ANY child strategy fires.</item>
    /// </list>
    /// </summary>
    public string Type { get; init; } = "composite";

    /// <summary>
    /// Regex pattern (required for <c>regex</c> type).
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Hard iteration cap. 0 means no cap (default).
    /// </summary>
    public int MaxIterations { get; init; } = 0;

    /// <summary>
    /// If set, only messages from these agents are evaluated for termination.
    /// Applies to <c>regex</c> type.
    /// </summary>
    public string[]? AgentNames { get; init; }

    /// <summary>
    /// Child strategies for the <c>composite</c> type.
    /// </summary>
    public List<TerminationStrategyConfig>? Strategies { get; init; }

    /// <summary>
    /// Optional validator name to run before this strategy fires.
    /// When set, the strategy only terminates after the named validator passes.
    /// If validation fails the error is injected into the conversation and the agent
    /// is re-invoked, giving it a chance to correct before termination is allowed.
    /// Built-in validators: <c>"RequireShellPass"</c>, <c>"RequireWriteFile"</c>,
    /// <c>"TestReportValid"</c>, <c>"RequireReviewJudgement"</c>, <c>"RequireRelatedTestsPass"</c>.
    /// Only meaningful on
    /// <c>regex</c> strategies; ignored on <c>maxiterations</c>.
    /// </summary>
    public string? Validator { get; init; }

    /// <summary>
    /// Multiple validators (AND semantics) — all must pass before termination is allowed.
    /// Takes precedence over the single <see cref="Validator"/> field when both are set.
    /// </summary>
    public List<string>? Validators { get; init; }

    /// <summary>
    /// Returns the effective iteration cap for this config.  Checks <see cref="MaxIterations"/>
    /// first; if that is 0, falls back to the first <c>maxiterations</c> child strategy so that
    /// a cap declared inside a <c>composite</c> block is honoured by the orchestrator hard-cap
    /// checks in addition to being used as a termination condition.
    /// Returns 0 when no cap is configured at any level.
    /// </summary>
    public int ResolveMaxIterations()
    {
        if (MaxIterations > 0) return MaxIterations;
        return Strategies?
            .FirstOrDefault(s => string.Equals(s.Type, "maxiterations", StringComparison.OrdinalIgnoreCase))
            ?.MaxIterations ?? 0;
    }
}
