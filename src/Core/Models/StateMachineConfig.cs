namespace fuseraft.Core.Models;

/// <summary>
/// Configures an explicit state graph for agent routing.
///
/// <para>
/// Instead of scanning for keywords in agent messages (which is fragile and language-
/// dependent), the state machine tracks the orchestration's current position in a
/// declared graph. Agents emit <em>signals</em> (keywords or structured output) that
/// the engine matches against the current state's outgoing transitions. Transitions
/// require both a matching signal AND all declared contracts to be satisfied.
/// </para>
///
/// <para>
/// Agents do not control flow — they only emit signals. The state machine resolves the
/// next state, which eliminates an entire class of routing hallucinations.
/// </para>
///
/// Example YAML:
/// <code>
/// Selection:
///   Type: statemachine
///   StateMachine:
///     Initial: Planning
///     States:
///       Planning:
///         Agent: Planner
///         Transitions:
///           - To: Implementation
///             Signal: "HANDOFF TO DEVELOPER"
///             Contracts: [BriefExists]
///       Implementation:
///         Agent: Developer
///         Transitions:
///           - To: Testing
///             Signal: "HANDOFF TO TESTER"
///             Contracts: [ImplementationComplete]
///           - To: Planning
///             Signal: "REPLAN REQUIRED"
///       Testing:
///         Agent: Tester
///         Transitions:
///           - To: Review
///             Signal: "HANDOFF TO REVIEWER"
///             Contracts: [TestsValid]
///           - To: Implementation
///             Signal: "BUGS FOUND"
///       Review:
///         Agent: Reviewer
///         Transitions:
///           - To: Done
///             Signal: "APPROVED"
///             Contracts: [ReviewApproved]
///           - To: Implementation
///             Signal: "REVISION REQUIRED"
///       Done:
///         Agent: Reviewer
///         Terminal: true
/// </code>
/// </summary>
public record StateMachineConfig
{
    /// <summary>
    /// Name of the state in <see cref="States"/> where orchestration begins.
    /// </summary>
    public string Initial { get; init; } = string.Empty;

    /// <summary>
    /// State definitions keyed by state name.
    /// </summary>
    public Dictionary<string, StateConfig> States { get; init; } = [];
}

/// <summary>
/// A single state in the state machine, representing one phase of the workflow.
/// </summary>
public record StateConfig
{
    /// <summary>
    /// Agent responsible for work in this state. Must match an agent name in
    /// <c>Orchestration.Agents</c>.
    /// </summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>
    /// Outgoing transitions evaluated after each turn in this state.
    /// Evaluated in order — the first transition whose signal is present AND whose
    /// contracts all pass fires immediately.
    /// </summary>
    public List<TransitionConfig> Transitions { get; init; } = [];

    /// <summary>
    /// When <c>true</c>, this is a terminal state. No transitions are evaluated and
    /// the state machine signals that the workflow is complete. The orchestrator's
    /// termination condition may still need to match independently.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool Terminal { get; init; } = false;
}

/// <summary>
/// One data source used in <see cref="TransitionConfig.HandoffContext"/> (what to inject
/// when a transition fires) and in <c>AgentConfig.Context</c> (what to assemble as the
/// agent's context at invocation time instead of replaying shared history).
/// </summary>
public record ContextSource
{
    /// <summary>
    /// Source identifier. Supported forms:
    /// <list type="bullet">
    ///   <item><c>session_context</c> — the handoff summary written by the previous agent via <c>session_context_write</c>.</item>
    ///   <item><c>changes_recent</c> or <c>changes_recent:N</c> — the last N change-log entries (default N = 3).</item>
    ///   <item><c>brief_field:FIELD</c> — a top-level field from brief.json (e.g. <c>brief_field:test_targets</c>).</item>
    ///   <item><c>file:PATH</c> — content of a file at PATH relative to the sandbox root.</item>
    ///   <item><c>own_history:N</c> — the agent's own last N turns from the shared history
    ///     (text-only, no tool frames). Only meaningful in <c>AgentConfig.Context</c>;
    ///     ignored in <c>TransitionConfig.HandoffContext</c>.</item>
    /// </list>
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Maximum characters to include from this source. Content exceeding the limit is
    /// truncated with an annotation showing the omitted character count.
    /// Defaults to 4,000 characters when not set.
    /// </summary>
    public int MaxChars { get; init; } = 0;

    /// <summary>Section header label. Defaults to a name derived from the source type.</summary>
    public string? Label { get; init; }
}

/// <summary>Alias kept for backward YAML compatibility — same as <see cref="ContextSource"/>.</summary>
public record HandoffContextSource : ContextSource;

/// <summary>
/// A directed edge in the state graph. Fires when the current state's agent emits
/// the declared <see cref="Signal"/> AND all <see cref="Contracts"/> are satisfied.
///
/// <para>
/// For parallel fan-out set <see cref="Parallel"/> to <c>true</c>, list target states
/// in <see cref="Targets"/>, and set <see cref="To"/> to the join state that receives
/// control after all branches finish and their outputs are merged.
/// </para>
///
/// Example YAML (parallel fan-out):
/// <code>
/// Transitions:
///   - To: Integration          # fan-in join state
///     Targets:                 # parallel branch states
///       - BackendImplementation
///       - FrontendImplementation
///       - MigrationPlanning
///     Parallel: true
///     Signal: "IMPLEMENT"
///     Merge:
///       Strategy: union
/// </code>
/// </summary>
public record TransitionConfig
{
    /// <summary>
    /// Target state name for a normal (sequential) transition, or the join state
    /// after a parallel fan-out completes. Must exist in
    /// <see cref="StateMachineConfig.States"/>.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Parallel branch target states. When <see cref="Parallel"/> is <c>true</c> and
    /// this list is non-empty, all named states run concurrently (one turn each with
    /// isolated history snapshots). <see cref="To"/> then acts as the fan-in join state
    /// entered after branch outputs are merged.
    /// </summary>
    public List<string>? Targets { get; init; }

    /// <summary>
    /// When <c>true</c>, this transition fans out to all states in <see cref="Targets"/>
    /// concurrently instead of routing to a single state. Each branch runs one agent
    /// turn with an isolated history snapshot; outputs are merged via <see cref="Merge"/>
    /// before control advances to the join state in <see cref="To"/>.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool Parallel { get; init; } = false;

    /// <summary>
    /// How to combine branch outputs when <see cref="Parallel"/> is <c>true</c>.
    /// Defaults to <see cref="MergeStrategy.Union"/> (concatenate in declaration order)
    /// when null.
    /// </summary>
    public MergeConfig? Merge { get; init; }

    /// <summary>
    /// Keyword or phrase the agent must emit (on its own line) to trigger this transition.
    /// Case-insensitive substring matching is used, consistent with keyword routing.
    /// When null or empty, the transition fires on any turn from this state that
    /// satisfies the contract gates — useful for automatic advance on contract satisfaction.
    /// </summary>
    public string? Signal { get; init; }

    /// <summary>
    /// Single contract name that must be satisfied for this transition to fire.
    /// Shorthand for <see cref="Contracts"/> when only one contract is needed.
    /// </summary>
    public string? Contract { get; init; }

    /// <summary>
    /// Names of contracts that must ALL be satisfied for this transition to fire (AND
    /// semantics). Evaluated after <see cref="Signal"/> presence is confirmed. If any
    /// contract fails, the transition is blocked and the source agent is re-invoked
    /// with the contract's error message.
    /// </summary>
    public List<string>? Contracts { get; init; }

    /// <summary>
    /// Optional list of agent names permitted to emit this transition's signal.
    /// When set, the signal is only accepted when the emitting agent is in this list.
    /// When null or empty, any agent may trigger the transition.
    /// </summary>
    public List<string>? SourceAgents { get; init; }

    /// <summary>
    /// Optional agent to invoke when this transition's contract fails repeatedly.
    /// When <see cref="FailureHandlingConfig"/> action is <c>ActivateRecovery</c> or
    /// the failure count reaches two, the recovery agent is selected instead of
    /// re-invoking the current state's agent. Fires at most once per
    /// state/transition pair to prevent infinite recovery loops.
    /// </summary>
    public string? RecoveryAgent { get; init; }

    /// <summary>
    /// Targeted artifact sources to inject as context for the receiving agent when this
    /// transition fires. When set, the orchestrator reads each source from durable disk
    /// artifacts and injects a compact block into history immediately after the turn-boundary
    /// marker. The receiving agent sees relevant facts without the full session transcript.
    ///
    /// <para>
    /// Example YAML:
    /// <code>
    /// - To: Testing
    ///   Signal: "HANDOFF TO TESTER"
    ///   Contract: ImplementationComplete
    ///   HandoffContext:
    ///     - Source: session_context
    ///     - Source: changes_recent
    ///     - Source: brief_field:test_targets
    ///     - Source: file:.fuseraft/artifacts/test-report.json
    ///       MaxChars: 2000
    /// </code>
    /// </para>
    /// </summary>
    public List<ContextSource>? HandoffContext { get; init; }

    /// <summary>
    /// Maximum times this transition may fire as a back-edge (i.e. routing back to a state
    /// that already ran) before an escalation message is injected naming the outstanding
    /// objections from the prior review artifact. 0 (default) disables the cap.
    ///
    /// <para>
    /// When the threshold is exceeded the agent is re-invoked with a message listing each
    /// objection explicitly rather than force-approving — the Critic's quality guarantee
    /// is preserved while the loop is broken.
    /// </para>
    /// </summary>
    public int MaxRevisits { get; init; } = 0;

    /// <summary>
    /// Path to the artifact file containing the reviewer's objections, injected into the
    /// escalation message when <see cref="MaxRevisits"/> is exceeded. Relative to the
    /// sandbox root. When null the escalation message is generic.
    /// </summary>
    public string? ReviewArtifactPath { get; init; }

    /// <summary>Returns all contract names declared on this transition (Contract + Contracts merged).</summary>
    internal IReadOnlyList<string> AllContracts
    {
        get
        {
            if (Contract is null && (Contracts is null or { Count: 0 }))
                return [];

            var list = new List<string>();
            if (Contract is not null) list.Add(Contract);
            if (Contracts is not null) list.AddRange(Contracts);
            return list;
        }
    }
}
