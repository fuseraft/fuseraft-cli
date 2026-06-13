using AgentGovernance;
using AgentGovernance.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Contracts;
using fuseraft.Orchestration.Failure;
using fuseraft.Orchestration.Parallel;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Explicit state-machine agent selection strategy.
///
/// <para>
/// Replaces string-scanning keyword routing with a declared state graph where:
/// <list type="bullet">
///   <item>The orchestrator tracks the current state explicitly.</item>
///   <item>Agents emit signals (keywords) that are matched against the current state's
///       outgoing transitions — not searched across all routes simultaneously.</item>
///   <item>Transitions require the signal AND all declared evidence contracts to pass.</item>
///   <item>Agents do not control flow — the state machine resolves transitions. This
///       eliminates routing hallucinations.</item>
/// </list>
/// </para>
///
/// <para>
/// Signal detection reuses the same line-boundary matching as
/// <see cref="KeywordSelectionStrategy"/> so existing agent instructions need
/// minimal changes when migrating from keyword routing to state machine routing.
/// </para>
/// </summary>
public sealed class StateMachineSelectionStrategy : IAgentSelector, IParallelAgentSelector, IContextSnapshotter
{
    private readonly StateMachineConfig _machine;
    private readonly ContractEngine? _contractEngine;
    private readonly FailureHandlingConfig _failureHandling;
    private readonly EventEmitter? _eventEmitter;
    private readonly ILogger<StateMachineSelectionStrategy> _logger;
    private readonly GovernanceKernel? _governance;
    private readonly ContextAssembler? _handoffResolver;
    private string _sessionId = "unknown";
    private IList<ChatMessage>? _history;

    // Current state name — mutated on each successful transition.
    private string _currentState;

    // Tracks consecutive transition failures keyed by "{state}::{transitionTo}".
    private (string Key, int Count, string LastError)? _transitionFailure;

    // Tracks consecutive turns in the current state without any matching signal.
    // Stored in strategy state (not history) so it survives compaction cycles.
    // Resets on successful transition or when the agent emits any valid signal.
    private (string State, int Count)? _noSignalFailure;

    // Tracks which state+transition pairs have already had their recovery logic fire.
    private readonly HashSet<string> _recoveryActivated = new(StringComparer.OrdinalIgnoreCase);

    // Counts how many times each specific back-edge (sourceState→targetState where
    // target already ran) has fired. Key format: "FromState::ToState".
    // Used to inject escalation prompts when MaxRevisits is exceeded.
    private readonly Dictionary<string, int> _backEdgeVisits = new(StringComparer.OrdinalIgnoreCase);

    // States visited in the current session (accumulated on each successful transition).
    // Used to detect back-edge signals to already-completed states.
    private readonly HashSet<string> _visitedStates = new(StringComparer.OrdinalIgnoreCase);

    // Verifier support.
    private readonly string? _verifierAgentName;
    private readonly bool _triggerVerifierOnConflict;
    private bool _runVerifierNext;

    public StateMachineSelectionStrategy(
        StateMachineConfig machine,
        ContractEngine? contractEngine = null,
        FailureHandlingConfig? failureHandling = null,
        EventEmitter? eventEmitter = null,
        ILogger<StateMachineSelectionStrategy>? logger = null,
        GovernanceKernel? governanceKernel = null,
        VerifierConfig? verifier = null,
        ContextAssembler? handoffResolver = null)
    {
        _machine         = machine;
        _contractEngine  = contractEngine;
        _failureHandling = failureHandling ?? new FailureHandlingConfig();
        _eventEmitter    = eventEmitter;
        _logger          = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StateMachineSelectionStrategy>.Instance;
        _governance      = governanceKernel;
        _handoffResolver = handoffResolver;
        _currentState    = machine.Initial;

        _verifierAgentName         = string.IsNullOrWhiteSpace(verifier?.AgentName) ? null : verifier!.AgentName;
        _triggerVerifierOnConflict = verifier?.TriggerOnSuspiciousTransition ?? true;
    }

    /// <summary>Returns the name of the state the machine is currently in.</summary>
    public string CurrentState => _currentState;

    /// <summary>
    /// Overrides the current state. Used after compaction to restore the machine to the
    /// state it was in before the history was trimmed, preventing a spurious reset to the
    /// initial state (typically Planning) when the correct current state is e.g. Testing.
    /// No-op when <paramref name="stateName"/> is not a valid state in the machine.
    /// </summary>
    public void SetCurrentState(string stateName)
    {
        if (_machine.States.ContainsKey(stateName))
        {
            _logger.LogDebug("[StateMachine] SetCurrentState: restoring state '{State}' after compaction", stateName);
            _currentState = stateName;
            // If we're resuming past the initial state, the initial state was already
            // visited — seed _visitedStates so the replan guard fires correctly.
            if (!string.Equals(stateName, _machine.Initial, StringComparison.OrdinalIgnoreCase))
                _visitedStates.Add(_machine.Initial);
        }
        else
        {
            _logger.LogWarning(
                "[StateMachine] SetCurrentState: '{State}' is not a known state — ignoring (valid: {Valid})",
                stateName, string.Join(", ", _machine.States.Keys));
        }
    }

    /// <summary>
    /// Provides the shared history reference used to inject correction messages.
    /// Must be called before the orchestration loop begins.
    /// </summary>
    public void SetHistory(IList<ChatMessage> history) => _history = history;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _handoffResolver?.SetSessionId(sessionId);
    }

    public async Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Verifier turn requested by a prior ConflictingEvidence / NoProgress failure.
        if (_runVerifierNext && _verifierAgentName is not null)
        {
            _runVerifierNext = false;
            var verifierAgent = FindAgent(agents, _verifierAgentName);
            if (verifierAgent is not null)
            {
                _logger.LogDebug("[StateMachine] Verifier turn — selecting '{Verifier}' before re-entering state '{State}'",
                    _verifierAgentName, _currentState);
                return verifierAgent;
            }
        }

        // Resolve current state.
        if (!_machine.States.TryGetValue(_currentState, out var state))
            throw new InvalidOperationException(
                $"[StateMachine] Current state '{_currentState}' is not defined in the state machine config.");

        // Terminal state: no further transitions — re-invoke the current agent so the
        // termination condition (regex/maxiterations) can fire naturally.
        if (state.Terminal)
        {
            _logger.LogDebug("[StateMachine] State '{State}' is terminal — awaiting termination condition",
                _currentState);
            return FindAgent(agents, state.Agent);
        }

        // Scan the last few agent messages for signals from the current state's agent.
        int scanned = 0;
        for (int i = history.Count - 1; i >= 0 && scanned < OrchestratorHelpers.AgentMessageLookback; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;

            // Extract HandoffPlugin keyword if present (same logic as keyword strategy).
            string? toolSignal = null;
            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var item in msg.Contents)
                {
                    if (item is FunctionCallContent fc
                        && string.Equals(fc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)
                        && fc.Arguments?.TryGetValue(HandoffPlugin.ArgumentName, out var kwObj) == true
                        && kwObj?.ToString() is { Length: > 0 } kw)
                    {
                        toolSignal = kw;
                        break;
                    }
                }
            }

            var content = toolSignal ?? msg.Text;
            if (string.IsNullOrEmpty(content)) continue;
            if (msg.Role == ChatRole.Assistant) scanned++;

            // Source-agent restriction: if the message isn't from the current state's
            // agent, it cannot trigger transitions (prevents ghost signals from other
            // agents bleeding through the lookback window).
            bool isCurrentAgent = string.Equals(
                msg.AuthorName, state.Agent, StringComparison.OrdinalIgnoreCase);

            foreach (var transition in state.Transitions)
            {
                // Signal check.
                bool signalPresent = string.IsNullOrWhiteSpace(transition.Signal)
                    || (toolSignal is not null
                        ? string.Equals(toolSignal, transition.Signal, StringComparison.OrdinalIgnoreCase)
                        : IsSignalOnOwnLine(content, transition.Signal!));

                if (!signalPresent) continue;

                // SourceAgents restriction on the transition itself.
                if (transition.SourceAgents is { Count: > 0 } &&
                    !transition.SourceAgents.Any(s =>
                        string.Equals(s, msg.AuthorName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug(
                        "[StateMachine] Signal '{Signal}' → '{To}' skipped: SourceAgents [{Allowed}] does not include '{Author}'",
                        transition.Signal, transition.To,
                        string.Join(",", transition.SourceAgents), msg.AuthorName ?? "(null)");
                    continue;
                }

                // Stale signal guard: skip if a turn-boundary marker already exists after
                // this message index, indicating the signal was consumed in a prior turn.
                if (transition.Signal is not null
                    && TransitionAlreadyFired(history, i, transition.To))
                {
                    _logger.LogDebug(
                        "[StateMachine] Signal '{Signal}' → '{To}' already consumed — skipping",
                        transition.Signal, transition.To);
                    continue;
                }

                // Block no-contract back-edge signals (e.g. "REPLAN REQUIRED") while a
                // forward transition from this state has an active contract failure.
                // Re-entering a prior state under these conditions wastes a full planning
                // cycle without resolving the underlying issue — inject a correction instead.
                if (transition.AllContracts.Count == 0
                    && _visitedStates.Contains(transition.To)
                    && _transitionFailure is { } blockedFailure
                    && blockedFailure.Key.StartsWith(_currentState + "::", StringComparison.OrdinalIgnoreCase))
                {
                    var blockedTo = blockedFailure.Key[(_currentState.Length + 2)..];

                    if (_history is not null)
                        _history.Add(new ChatMessage(ChatRole.User,
                            $"REPLAN BLOCKED — '{_currentState}' → '{blockedTo}' has {blockedFailure.Count} consecutive " +
                            $"contract failure(s). Fix the contract first; routing back to '{transition.To}' is not allowed " +
                            $"until the forward path is clear.\n\n" +
                            blockedFailure.LastError));

                    if (_eventEmitter is not null)
                        _ = _eventEmitter.EmitAsync("replan_blocked",
                            agent: state.Agent,
                            payload: new { from = _currentState, to = transition.To, blocked_transition = blockedTo, consecutive = blockedFailure.Count });

                    _logger.LogDebug(
                        "[StateMachine] Blocked back-edge '{From}' → '{To}': active contract failure on '{CurrentState}' → '{BlockedTo}'",
                        _currentState, transition.To, _currentState, blockedTo);

                    return FindAgent(agents, state.Agent)
                           ?? throw new InvalidOperationException(
                               $"[StateMachine] Agent '{state.Agent}' not found in pool for state '{_currentState}'.");
                }

                _logger.LogDebug(
                    "[StateMachine] Signal '{Signal}' matched → evaluating transition '{From}' → '{To}'",
                    transition.Signal ?? "(auto)", _currentState, transition.To);

                // Contract evaluation — all must pass (AND semantics).
                var contractNames = transition.AllContracts;
                if (contractNames.Count > 0 && _contractEngine is not null)
                {
                    string? failureError = null;
                    string? failingContract = null;

                    foreach (var contractName in contractNames)
                    {
                        var (ok, error) = await _contractEngine.EvaluateAsync(contractName, cancellationToken);
                        if (!ok)
                        {
                            failureError    = error;
                            failingContract = contractName;
                            break;
                        }
                    }

                    if (failureError is not null)
                    {
                        var recovery = await HandleTransitionFailureAsync(
                            state, transition, failingContract!, failureError,
                            agents, history, msg.AuthorName, cancellationToken);

                        if (recovery is not null) return recovery;

                        // Re-invoke the current state's agent.
                        return FindAgent(agents, state.Agent)
                               ?? throw new InvalidOperationException(
                                   $"[StateMachine] Agent '{state.Agent}' not found in pool for state '{_currentState}'.");
                    }
                }

                // All contracts satisfied — transition.
                var targetState = transition.To;
                if (!_machine.States.TryGetValue(targetState, out var nextState))
                    throw new InvalidOperationException(
                        $"[StateMachine] Transition target state '{targetState}' is not defined.");

                // Back-edge revisit guard: when this transition returns to a previously-visited
                // state and MaxRevisits is configured, track the visit count and inject an
                // escalation message once the threshold is exceeded. This breaks Planning loops
                // without force-approving — the Critic's objections are surfaced explicitly.
                if (transition.MaxRevisits > 0 && _history is not null)
                {
                    var backEdgeKey = $"{_currentState}::{targetState}";
                    _backEdgeVisits.TryGetValue(backEdgeKey, out var priorVisits);
                    var newVisits = priorVisits + 1;
                    _backEdgeVisits[backEdgeKey] = newVisits;

                    if (newVisits > transition.MaxRevisits)
                    {
                        _logger.LogWarning(
                            "[StateMachine] Back-edge '{From}' → '{To}' has fired {Count} times (MaxRevisits={Max}) — injecting escalation.",
                            _currentState, targetState, newVisits, transition.MaxRevisits);

                        string objections = string.Empty;
                        if (transition.ReviewArtifactPath is { Length: > 0 } artifactPath
                            && File.Exists(artifactPath))
                        {
                            try { objections = await File.ReadAllTextAsync(artifactPath, cancellationToken); }
                            catch { /* best-effort */ }
                        }

                        var escalation =
                            $"You have received the same critique {newVisits} times (limit: {transition.MaxRevisits}). " +
                            $"This is escalation attempt {newVisits - transition.MaxRevisits}.\n\n" +
                            (objections.Length > 0
                                ? $"Outstanding objections from the last review:\n{objections.Trim()}\n\n"
                                : string.Empty) +
                            $"Produce a revised brief that explicitly addresses each objection above, " +
                            $"or emit \"REPLAN REQUIRED\" if the task is not achievable as specified.";

                        _history.Add(new ChatMessage(ChatRole.User, escalation));

                        if (_eventEmitter is not null)
                            _ = _eventEmitter.EmitAsync("back_edge_escalation",
                                payload: new { from = _currentState, to = targetState, visit_count = newVisits, max_revisits = transition.MaxRevisits });
                    }
                }

                // Clear failure trackers on successful transition.
                _transitionFailure = null;
                _noSignalFailure   = null;

                // Inject turn-boundary marker when agent changes, followed by any
                // handoff context assembled from durable artifacts.
                if (_history is not null &&
                    !string.Equals(state.Agent, nextState.Agent, StringComparison.OrdinalIgnoreCase))
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {state.Agent} → {nextState.Agent}]"));

                    if (transition.HandoffContext is { Count: > 0 } hcSources
                        && _handoffResolver is not null)
                    {
                        try
                        {
                            var hcText = await _handoffResolver.ResolveAsync(
                                nextState.Agent, hcSources, cancellationToken);
                            if (hcText is not null)
                                _history.Add(new ChatMessage(ChatRole.User, hcText));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[StateMachine] HandoffContext resolution failed for transition '{From}' → '{To}' — continuing without injected context.",
                                _currentState, targetState);
                        }
                    }
                }

                _logger.LogDebug(
                    "[StateMachine] Transition fired: '{From}' → '{To}' (agent: {From_Agent} → {To_Agent})",
                    _currentState, targetState, state.Agent, nextState.Agent);

                _visitedStates.Add(_currentState);
                _currentState = targetState;
                return FindAgent(agents, nextState.Agent)
                       ?? throw new InvalidOperationException(
                           $"[StateMachine] Agent '{nextState.Agent}' not found in pool for state '{targetState}'.");
            }
        }

        // BLOCKED: agent declared an unrecoverable blocker — halt immediately, no correction loop.
        var lastAssistantText = history
            .LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?.Text;
        if (lastAssistantText is not null && IsSignalOnOwnLine(lastAssistantText, "BLOCKED"))
            throw new AgentBlockedException(state.Agent, lastAssistantText);

        // No signal matched — re-invoke the current state's agent with corrective nudge if needed.
        _logger.LogDebug(
            "[StateMachine] No transition signal matched in state '{State}' — re-invoking agent '{Agent}'",
            _currentState, state.Agent);

        if (_eventEmitter is not null)
        {
            var expectedSignals = state.Transitions
                .Where(t => !string.IsNullOrWhiteSpace(t.Signal))
                .Select(t => t.Signal!)
                .Distinct()
                .ToList();
            _ = _eventEmitter.EmitAsync(EventTypes.KeywordNotFound,
                agent: state.Agent,
                payload: new { state = _currentState, agent = state.Agent, expected_signals = expectedSignals });
        }

        // Accumulate consecutive no-signal turns in strategy state so the counter
        // survives compaction (unlike the history-scan used by InjectLoopWarningIfNeeded).
        var noSigCount = _noSignalFailure?.State == _currentState
            ? _noSignalFailure.Value.Count + 1
            : 1;
        _noSignalFailure = (_currentState, noSigCount);

        if (_failureHandling.MaxConsecutiveTurnsWithoutSignal > 0
            && noSigCount >= _failureHandling.MaxConsecutiveTurnsWithoutSignal)
        {
            _noSignalFailure = null;
            var validSignals = string.Join(", ", state.Transitions
                .Where(t => !string.IsNullOrWhiteSpace(t.Signal))
                .Select(t => $"'{t.Signal}'")
                .Distinct());
            throw new ValidatorStuckException(
                agentName:           state.Agent,
                validatorName:       $"signal-required:{_currentState}",
                consecutiveFailures: noSigCount,
                lastValidatorError:
                    $"Agent '{state.Agent}' completed {noSigCount} consecutive turns in state '{_currentState}' " +
                    $"without emitting a routing signal. Required signal(s): {validSignals}. " +
                    $"The agent may have completed its work but is not calling handoff correctly.");
        }

        InjectLoopWarningIfNeeded(history, state.Agent);
        InjectMissingSignalCorrectionIfNeeded(history, state);

        var current = FindAgent(agents, state.Agent);
        return current
               ?? throw new InvalidOperationException(
                   $"[StateMachine] Agent '{state.Agent}' not found in pool for state '{_currentState}'.");
    }

    // IParallelAgentSelector ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<ParallelAgentBatch?> TrySelectParallelAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (!_machine.States.TryGetValue(_currentState, out var state) || state.Terminal)
            return Task.FromResult<ParallelAgentBatch?>(null);

        int scanned = 0;
        for (int i = history.Count - 1; i >= 0 && scanned < OrchestratorHelpers.AgentMessageLookback; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;

            string? toolSignal = null;
            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var item in msg.Contents)
                {
                    if (item is FunctionCallContent fc
                        && string.Equals(fc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)
                        && fc.Arguments?.TryGetValue(HandoffPlugin.ArgumentName, out var kwObj) == true
                        && kwObj?.ToString() is { Length: > 0 } kw)
                    {
                        toolSignal = kw;
                        break;
                    }
                }
            }

            var content = toolSignal ?? msg.Text;
            if (string.IsNullOrEmpty(content)) continue;
            if (msg.Role == ChatRole.Assistant) scanned++;

            foreach (var transition in state.Transitions)
            {
                if (!transition.Parallel || transition.Targets is null or { Count: 0 }) continue;

                bool signalPresent = string.IsNullOrWhiteSpace(transition.Signal)
                    || (toolSignal is not null
                        ? string.Equals(toolSignal, transition.Signal, StringComparison.OrdinalIgnoreCase)
                        : IsSignalOnOwnLine(content, transition.Signal!));

                if (!signalPresent) continue;

                if (transition.Signal is not null && TransitionAlreadyFired(history, i, transition.To))
                {
                    _logger.LogDebug(
                        "[StateMachine] Parallel signal '{Signal}' → '{Join}' already consumed — skipping",
                        transition.Signal, transition.To);
                    continue;
                }

                // Resolve branch agents.
                var branches = new List<(AIAgent Agent, string StateName)>();
                foreach (var targetName in transition.Targets)
                {
                    if (!_machine.States.TryGetValue(targetName, out var targetState))
                        throw new InvalidOperationException(
                            $"[StateMachine] Parallel target state '{targetName}' is not defined.");

                    var branchAgent = FindAgent(agents, targetState.Agent)
                        ?? throw new InvalidOperationException(
                            $"[StateMachine] Agent '{targetState.Agent}' not found for parallel state '{targetName}'.");

                    branches.Add((branchAgent, targetName));
                }

                var joinState = transition.To;
                if (!_machine.States.ContainsKey(joinState))
                    throw new InvalidOperationException(
                        $"[StateMachine] Parallel join state '{joinState}' is not defined.");

                // Inject boundary marker and advance state before returning the batch.
                if (_history is not null)
                {
                    var branchList = string.Join(", ", transition.Targets);
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {state.Agent} → parallel({branchList}) → {joinState}]"));
                }

                _logger.LogDebug(
                    "[StateMachine] Parallel transition fired: '{From}' → [{Branches}] (join: '{Join}')",
                    _currentState, string.Join(", ", transition.Targets), joinState);

                _currentState = joinState;

                return Task.FromResult<ParallelAgentBatch?>(
                    new ParallelAgentBatch(branches, transition.Merge ?? new MergeConfig(), joinState));
            }
        }

        return Task.FromResult<ParallelAgentBatch?>(null);
    }

    // Handles a transition contract failure: classifies it, emits events, injects
    // a correction message, and potentially escalates to HITL or routes to a recovery agent.
    // Returns the recovery agent when ActivateRecovery fires; null otherwise (caller re-invokes
    // the current state's agent).
    private async Task<AIAgent?> HandleTransitionFailureAsync(
        StateConfig state,
        TransitionConfig transition,
        string failingContract,
        string errorMessage,
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        string? authorName,
        CancellationToken cancellationToken)
    {
        // Agent emitted a signal (contract blocked it), so silence counter resets.
        _noSignalFailure = null;

        var failureKey = $"{_currentState}::{transition.To}";
        var newCount = _transitionFailure?.Key == failureKey
            ? _transitionFailure.Value.Count + 1
            : 1;
        _transitionFailure = (failureKey, newCount, errorMessage);

        if (_eventEmitter is not null)
            _ = _eventEmitter.EmitAsync(EventTypes.ValidationFail,
                agent: authorName,
                payload: new { contract = failingContract, state = _currentState, transition = transition.To, consecutive = newCount, error = errorMessage });

        if (_governance is not null)
        {
            _governance.AuditEmitter.Emit(
                GovernanceEventType.PolicyViolation,
                agentId:   authorName ?? state.Agent,
                sessionId: _sessionId,
                data: new Dictionary<string, object>
                {
                    ["agent_name"]  = state.Agent,
                    ["contract"]    = failingContract,
                    ["state"]       = _currentState,
                    ["transition"]  = transition.To,
                    ["consecutive"] = newCount,
                });
        }

        _governance?.SloEngine.Get("policy-compliance")?.Record(0.0);

        // Classify failure.
        bool hasToolCalls = false;
        if (newCount > 1)
        {
            for (int j = history.Count - 1; j >= 0; j--)
            {
                if (history[j].Role == ChatRole.User) break;
                if (history[j].Role == ChatRole.Tool) { hasToolCalls = true; break; }
            }
        }
        else
        {
            hasToolCalls = true;
        }

        var failureType = FailureClassifier.Classify(errorMessage, hasToolCalls, isFirstFailure: newCount == 1);
        var typeConfig  = _failureHandling.GetConfig(failureType);

        _logger.LogDebug(
            "[StateMachine] Contract '{Contract}' failed (consecutive={Count}) → failureType={Type} action={Action}",
            failingContract, newCount, failureType, typeConfig.Action);

        // Immediate escalation.
        if (typeConfig.Action == FailureAction.EscalateToHuman)
        {
            _transitionFailure = null;
            throw new ValidatorStuckException(
                agentName:           state.Agent,
                validatorName:       failingContract,
                consecutiveFailures: newCount,
                lastValidatorError:  errorMessage);
        }

        // Recovery agent activation — mirrors KeywordSelectionStrategy behavior.
        // Fires when ActivateRecovery is the configured action, or after 2+ consecutive
        // failures on a transition that declares a RecoveryAgent. At most once per
        // state/transition pair to prevent infinite recovery loops.
        bool recoveryRequested = typeConfig.Action == FailureAction.ActivateRecovery
            || (newCount >= 2 && transition.RecoveryAgent is not null);

        if (recoveryRequested
            && transition.RecoveryAgent is not null
            && !_recoveryActivated.Contains(failureKey))
        {
            var recoveryAgent = FindAgent(agents, transition.RecoveryAgent);

            if (recoveryAgent is not null)
            {
                _recoveryActivated.Add(failureKey);
                _transitionFailure = null;

                if (_history is not null)
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"RECOVERY ACTIVATED: '{transition.RecoveryAgent}' called — '{failingContract}' " +
                        $"failed {newCount}× on transition '{_currentState}' → '{transition.To}'.\n\n" +
                        $"  1. changes_read_latest — review what was attempted.\n" +
                        $"  2. Fix the problem described below.\n" +
                        $"  3. Emit '{transition.Signal}' when resolved.\n\n" +
                        $"Failure ({failureType}): {errorMessage}"));
                }

                _logger.LogDebug(
                    "[StateMachine] FailureType={FailureType} — activating recovery agent '{Recovery}'",
                    failureType, transition.RecoveryAgent);

                return recoveryAgent;
            }

            _logger.LogDebug(
                "[StateMachine] RecoveryAgent '{Recovery}' not found in agent pool — falling through",
                transition.RecoveryAgent);
        }

        // Threshold-based abort.
        if (typeConfig.Action == FailureAction.Abort && newCount >= typeConfig.Threshold)
        {
            _transitionFailure = null;
            throw new ValidatorStuckException(
                agentName:           state.Agent,
                validatorName:       failingContract,
                consecutiveFailures: newCount,
                lastValidatorError:  errorMessage);
        }

        // Schedule a verifier turn for ConflictingEvidence or NoProgress when configured.
        if (_triggerVerifierOnConflict
            && _verifierAgentName is not null
            && failureType is FailureType.ConflictingEvidence or FailureType.NoProgress)
        {
            _runVerifierNext = true;
            _logger.LogDebug(
                "[StateMachine] Scheduling verifier turn after {Type} failure on contract '{Contract}'",
                failureType, failingContract);
        }

        // Global backstop: escalate to HITL when any transition has failed too many
        // times regardless of the per-type action. This prevents Reinstruct policies
        // from looping indefinitely when a contract cannot be satisfied.
        if (_failureHandling.MaxConsecutiveContractFailures > 0
            && newCount >= _failureHandling.MaxConsecutiveContractFailures)
        {
            _transitionFailure = null;
            throw new ValidatorStuckException(
                agentName:           state.Agent,
                validatorName:       failingContract,
                consecutiveFailures: newCount,
                lastValidatorError:  $"[MaxConsecutiveContractFailures={_failureHandling.MaxConsecutiveContractFailures} reached] " + errorMessage);
        }

        // Inject correction.
        if (_history is not null)
        {
            var correction = BuildTransitionCorrectionMessage(
                failureType, typeConfig, newCount,
                errorMessage, failingContract, _currentState, transition.To, _sessionId);
            _history.Add(new ChatMessage(ChatRole.User, correction));

            // Blocking marker: prevents the signal that triggered this failed transition
            // from being re-evaluated on the next turn via the lookback window. The agent
            // must emit a fresh signal for another contract check. TransitionAlreadyFired
            // already checks for "[fuseraft:" prefix, so this marker is picked up naturally.
            _history.Add(new ChatMessage(ChatRole.User,
                $"[fuseraft:blocked {_currentState}→{transition.To}]"));
        }

        return null; // re-invoke the current state's agent
    }

    // Builds a targeted correction message for transition failures.
    private static string BuildTransitionCorrectionMessage(
        FailureType failureType,
        FailureTypeConfig typeConfig,
        int newCount,
        string errorMessage,
        string contractName,
        string fromState,
        string toState,
        string sessionId = "")
    {
        var prefix = newCount > 1
            ? $"RETRY {newCount}/{typeConfig.Threshold} — "
            : string.Empty;

        return failureType switch
        {
            FailureType.NoProgress =>
                $"CRITICAL: You re-emitted the signal to transition from '{fromState}' to '{toState}' " +
                $"without calling any tools. Your next response MUST begin with a tool call. " +
                $"Do not re-emit the signal until the contract below is satisfied.\n\n" +
                errorMessage,

            FailureType.MissingEvidence =>
                $"{prefix}MISSING ARTIFACT — Transition '{fromState}' → '{toState}' is blocked " +
                $"because contract '{contractName}' requires an artifact that does not exist yet.\n\n" +
                $"Steps to resolve:\n" +
                $"  1. Read {FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalBrief, sessionId)} to identify the required artifacts.\n" +
                $"  2. Create the missing artifact using write_file or the appropriate tool.\n" +
                $"  3. Re-emit the signal once the artifact exists.\n\n" +
                errorMessage,

            FailureType.ConflictingEvidence =>
                $"{prefix}EVIDENCE AUDIT REQUIRED — Transition '{fromState}' → '{toState}' is blocked " +
                $"because contract '{contractName}' detected an inconsistency.\n\n" +
                $"Mandatory audit steps:\n" +
                $"  1. Call changes_read_latest to review what was actually recorded.\n" +
                $"  2. Re-run any commands whose results you referenced.\n" +
                $"  3. Re-read all artifacts you claim to have produced.\n" +
                $"  4. Re-emit the signal only after you have verified every piece of evidence.\n\n" +
                errorMessage,

            _ => // InvalidTransition
                newCount > 1
                    ? $"RETRY {newCount}/{typeConfig.Threshold} — " +
                      $"Transition '{fromState}' → '{toState}' is still blocked. " +
                      $"Your previous attempt did not resolve the issue. " +
                      $"Do NOT repeat your last response — act on the instructions below:\n\n" +
                      errorMessage
                    : $"Transition '{fromState}' → '{toState}' is blocked by contract '{contractName}':\n\n" +
                      errorMessage,
        };
    }

    // Injects a missing-signal nudge when the agent's last turn ended without a
    // recognisable signal. Mirrors the equivalent behaviour in KeywordSelectionStrategy.
    private void InjectMissingSignalCorrectionIfNeeded(
        IList<ChatMessage> history,
        StateConfig state)
    {
        if (_history is null || state.Transitions.Count == 0) return;

        // Find the most recent agent text message.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (msg.Role == ChatRole.User) return;
            if (msg.Role != ChatRole.Assistant || string.IsNullOrEmpty(msg.Text)) continue;

            // Only nudge when the last agent in history is the current state's agent.
            if (!string.Equals(msg.AuthorName, state.Agent, StringComparison.OrdinalIgnoreCase))
                return;

            var signals = state.Transitions
                .Where(t => !string.IsNullOrWhiteSpace(t.Signal))
                .Select(t => $"'{t.Signal}'")
                .Distinct()
                .ToList();

            if (signals.Count == 0) return;

            _history.Add(new ChatMessage(ChatRole.User,
                $"Your last turn ended without emitting a required transition signal. " +
                $"If your work in state '{_currentState}' is complete, emit one of the " +
                $"following signals as the last line of your response: " +
                $"{string.Join(", ", signals)}. " +
                $"If work remains, complete it first (one tool call at a time), " +
                $"then end your response with the appropriate signal."));
            return;
        }
    }

    private void InjectLoopWarningIfNeeded(IList<ChatMessage> history, string agentName)
    {
        if (_history is null) return;

        int consecutive = OrchestratorHelpers.CountConsecutiveAgentTurns(history, agentName);

        if (consecutive > 0 && consecutive % OrchestratorHelpers.ConsecutiveTurnWarningThreshold == 0)
        {
            _history.Add(new ChatMessage(ChatRole.User,
                $"LOOP WARNING: {agentName} has been invoked {consecutive} consecutive turns " +
                $"in state '{_currentState}' without completing the required task. " +
                $"You appear to be stuck. Take these steps:\n" +
                $"  1. Call read_file on {FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalBrief, _sessionId)} to restore the task brief.\n" +
                $"  2. Call changes_read_latest to see what has already been done.\n" +
                $"  3. Identify the single blocking action and execute it now.\n" +
                $"  4. Emit the correct transition signal once that action is complete."));
        }
    }

    // Returns true when a keyword appears alone on its own line (same rules as KeywordSelectionStrategy).
    private static bool IsSignalOnOwnLine(string content, string signal)
    {
        foreach (var line in content.Split('\n'))
        {
            var stripped = line.Trim().Replace("*", "").Replace("_", "").Trim();
            if (string.Equals(stripped, signal, StringComparison.OrdinalIgnoreCase))
                return true;
            if (stripped.Length > signal.Length &&
                stripped.StartsWith(signal, StringComparison.OrdinalIgnoreCase) &&
                (char.IsWhiteSpace(stripped[signal.Length]) || char.IsPunctuation(stripped[signal.Length])))
                return true;
        }
        return false;
    }

    // Returns true when this specific transition was already consumed after signalIndex.
    //
    // Two marker types are checked:
    //   "[fuseraft:blocked {state}→{targetState}]" — the transition was evaluated and
    //     its contract failed; the signal must not be re-evaluated for that target.
    //     Markers for OTHER targets do not suppress this transition.
    //   Any other "[fuseraft: ...]" — a different transition fired, meaning the state
    //     machine already advanced; the signal is consumed regardless of target.
    private static bool TransitionAlreadyFired(IList<ChatMessage> history, int signalIndex, string targetState)
    {
        for (int j = signalIndex + 1; j < history.Count; j++)
        {
            var m = history[j];
            if (m.Role != ChatRole.User) continue;
            var text = m.Text;
            if (string.IsNullOrEmpty(text)) continue;
            if (!text.StartsWith("[fuseraft:", StringComparison.Ordinal)) continue;

            // Blocking markers suppress only the transition they name.
            // "[fuseraft:blocked A→B]" blocks A→B but must not block A→C.
            if (text.StartsWith("[fuseraft:blocked ", StringComparison.Ordinal))
            {
                if (text.Contains($"→{targetState}", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue; // Different target — does not apply to this transition.
            }

            // Any non-blocking marker means the state machine already acted on a signal
            // in this lookback window (transition fired or parallel dispatched).
            return true;
        }
        return false;
    }

    // IContextSnapshotter ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ContextSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var results = new List<ContractCheckResult>();
        if (_contractEngine is not null)
        {
            foreach (var name in _contractEngine.ContractNames)
            {
                var (ok, error) = await _contractEngine.EvaluateAsync(name, ct);
                results.Add(new ContractCheckResult(name, ok, error));
            }
        }

        List<EvidenceNode> recent = [];
        if (_contractEngine?.EvidenceStore is { } store)
        {
            var nodes = await store.QueryNodes(_ => true, ct);
            recent = [.. nodes.OrderByDescending(n => n.Timestamp).Take(30)];
        }

        return new ContextSnapshot
        {
            CurrentStateName  = _currentState,
            ContractResults   = results,
            RecentEvidence    = recent,
            SessionId         = _sessionId == "unknown" ? null : _sessionId,
            Timestamp         = DateTimeOffset.UtcNow,
            TransitionFailure = _transitionFailure,
            NoSignalFailure   = _noSignalFailure,
            VisitedStates     = _visitedStates,
            BackEdgeVisits    = _backEdgeVisits,
            RecoveryActivated = _recoveryActivated,
        };
    }

    /// <summary>
    /// Returns a JSON-serialisable checkpoint of the failure-tracking counters. Called by
    /// <see cref="fuseraft.Cli.SessionRunner"/> immediately before compaction so the counters
    /// survive across <c>StreamAsync</c> restarts.
    /// </summary>
    public StateMachineCheckpointState TakeCheckpointState() => new()
    {
        TransitionFailureKey   = _transitionFailure?.Key,
        TransitionFailureCount = _transitionFailure?.Count ?? 0,
        TransitionFailureError = _transitionFailure?.LastError,
        NoSignalFailureState   = _noSignalFailure?.State,
        NoSignalFailureCount   = _noSignalFailure?.Count ?? 0,
        VisitedStates          = [.. _visitedStates],
        BackEdgeVisits         = new Dictionary<string, int>(_backEdgeVisits, StringComparer.OrdinalIgnoreCase),
        RecoveryActivated      = [.. _recoveryActivated],
    };

    /// <summary>
    /// Restores the failure-tracking counters from a persisted checkpoint. Called after
    /// <see cref="SetCurrentState"/> during compaction resume so all five counters survive
    /// the <c>StreamAsync</c> restart rather than resetting to their zero-state defaults.
    /// No-op when <paramref name="snap"/> is null.
    /// </summary>
    public void RestoreFromSnapshot(StateMachineCheckpointState? snap)
    {
        if (snap is null) return;

        _transitionFailure = snap.TransitionFailureKey is { Length: > 0 }
            ? (snap.TransitionFailureKey, snap.TransitionFailureCount, snap.TransitionFailureError ?? string.Empty)
            : null;

        _noSignalFailure = snap.NoSignalFailureState is { Length: > 0 }
            ? (snap.NoSignalFailureState, snap.NoSignalFailureCount)
            : null;

        _visitedStates.Clear();
        foreach (var s in snap.VisitedStates) _visitedStates.Add(s);

        _backEdgeVisits.Clear();
        foreach (var (k, v) in snap.BackEdgeVisits) _backEdgeVisits[k] = v;

        _recoveryActivated.Clear();
        foreach (var s in snap.RecoveryActivated) _recoveryActivated.Add(s);

        _logger.LogDebug("[StateMachine] RestoreFromSnapshot: failure state restored from checkpoint (transition={Key}/{Count}, noSignal={NSState}/{NSCount}, visited={Visited}, backEdges={BackEdges}, recovered={Recovered})",
            _transitionFailure?.Key ?? "none", _transitionFailure?.Count ?? 0,
            _noSignalFailure?.State ?? "none", _noSignalFailure?.Count ?? 0,
            _visitedStates.Count, _backEdgeVisits.Count, _recoveryActivated.Count);
    }

    private static AIAgent? FindAgent(IReadOnlyList<AIAgent> agents, string name) =>
        agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}
