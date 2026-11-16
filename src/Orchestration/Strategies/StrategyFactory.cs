using Microsoft.Extensions.Logging;
using AgentGovernance;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Contracts;
using fuseraft.Orchestration.Validation;

namespace fuseraft.Orchestration.Strategies;

/// <summary>Builds the agent selector for a compiled Steps flow: the state machine and its contract engine.</summary>
public sealed class StrategyFactory(EventEmitter? eventEmitter = null, ILoggerFactory? loggerFactory = null, GovernanceKernel? governanceKernel = null, EvidenceStore? evidenceStore = null, TestSelectorConfig? testSelector = null, string? sandboxRoot = null, IHumanApprovalService? humanApprovalService = null)
{
    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;
    private readonly EventEmitter? _eventEmitter = eventEmitter;
    private readonly GovernanceKernel? _governanceKernel = governanceKernel;
    private readonly EvidenceStore? _evidenceStore = evidenceStore;
    private readonly TestSelectorConfig? _testSelector = testSelector;
    private readonly string? _sandboxRoot = sandboxRoot;
    private string _sessionId = string.Empty;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
    }

    /// <summary>Creates the selector for <paramref name="config"/>; only Steps flows (<c>statemachine</c>) use one.</summary>
    public IAgentSelector CreateSelection(
        SelectionStrategyConfig config,
        ValidationConfig? validationConfig = null,
        FailureHandlingConfig? failureHandling = null,
        IReadOnlyList<ContractConfig>? contracts = null,
        VerifierConfig? verifier = null)
    {
        if (!config.Type.Equals(OrchestratorTypes.StateMachine, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Flow type '{config.Type}' has its own orchestrator and no agent selector.");
        return CreateStateMachineSelection(config, validationConfig, failureHandling, contracts, verifier);
    }

    private StateMachineSelectionStrategy CreateStateMachineSelection(
        SelectionStrategyConfig config,
        ValidationConfig? validationConfig,
        FailureHandlingConfig? failureHandling,
        IReadOnlyList<ContractConfig>? contracts,
        VerifierConfig? verifier)
    {
        if (config.StateMachine is null)
            throw new InvalidOperationException("A Steps flow compiles to a state machine, but none was provided.");

        var sm = config.StateMachine;

        if (string.IsNullOrWhiteSpace(sm.Initial))
            throw new InvalidOperationException(
                "The compiled state machine has no initial state; flow.start should have set it.");

        if (!sm.States.ContainsKey(sm.Initial))
            throw new InvalidOperationException(
                $"The compiled state machine starts at '{sm.Initial}', which has no state.");

        // Validate all transition targets exist.
        foreach (var (stateName, state) in sm.States)
        {
            foreach (var transition in state.Transitions)
            {
                if (!sm.States.ContainsKey(transition.To))
                    throw new InvalidOperationException(
                        $"State '{stateName}' has a transition to undefined state '{transition.To}'; " +
                        $"every edge target needs its own entry under flow.steps.");
            }
        }

        // Validate that all contracts referenced by transitions are declared.
        if (contracts is null || contracts.Count == 0)
        {
            var referencedContracts = sm.States.Values
                .SelectMany(s => s.Transitions)
                .SelectMany(t => t.AllContracts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (referencedContracts.Count > 0)
                throw new InvalidOperationException(
                    $"State machine transitions reference contract(s) " +
                    $"{string.Join(", ", referencedContracts.Select(c => $"'{c}'"))} " +
                    $"but no contracts were compiled.");
        }

        ContractEngine? contractEngine = contracts is { Count: > 0 }
            ? new ContractEngine(contracts, validationConfig, _evidenceStore, _testSelector, _sandboxRoot, _sessionId,
                validatorFactory: p => ValidatorRegistry.Build(p.Validator ?? string.Empty, validationConfig, _sandboxRoot, p.Pattern, p.Pattern))
            : null;

        var strategyLogger = loggerFactory?.CreateLogger<StateMachineSelectionStrategy>();
        return new StateMachineSelectionStrategy(sm, contractEngine, failureHandling, _eventEmitter, strategyLogger, _governanceKernel, verifier, _humanApprovalService);
    }
}
