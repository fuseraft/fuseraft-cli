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
/// A directed edge in the state graph. Fires when the current state's agent emits
/// the declared <see cref="Signal"/> AND all <see cref="Contracts"/> are satisfied.
/// </summary>
public record TransitionConfig
{
    /// <summary>
    /// Target state name. Must exist in <see cref="StateMachineConfig.States"/>.
    /// </summary>
    public string To { get; init; } = string.Empty;

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
