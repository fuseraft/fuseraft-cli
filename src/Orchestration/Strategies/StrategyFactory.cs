using System.Text.RegularExpressions;
using AgentGovernance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration.Contracts;
using fuseraft.Orchestration.Validation;
using fuseraft.Orchestration;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Builds agent selection and termination strategies from configuration.
/// </summary>
public sealed class StrategyFactory(Func<ModelConfig, IChatClient> createChatClient, EventEmitter? eventEmitter = null, ILoggerFactory? loggerFactory = null, GovernanceKernel? governanceKernel = null, IHumanApprovalService? humanApprovalService = null, EvidenceStore? evidenceStore = null, ProvenanceRegistry? provenanceRegistry = null, TestSelectorConfig? testSelector = null, string? sandboxRoot = null, ContextAssembler? contextAssembler = null)
{
    private readonly EventEmitter? _eventEmitter = eventEmitter;
    private readonly GovernanceKernel? _governanceKernel = governanceKernel;
    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;
    private readonly EvidenceStore? _evidenceStore = evidenceStore;
    private readonly ProvenanceRegistry? _provenanceRegistry = provenanceRegistry;
    private readonly TestSelectorConfig? _testSelector = testSelector;
    private readonly string? _sandboxRoot = sandboxRoot;
    private readonly ContextAssembler? _contextAssembler = contextAssembler;
    private string _sessionId = string.Empty;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _contextAssembler?.SetSessionId(sessionId);
    }

    // Selection

    public IAgentSelector CreateSelection(
        SelectionStrategyConfig config,
        IReadOnlyList<AIAgent> agents,
        ValidationConfig? validationConfig = null,
        FailureHandlingConfig? failureHandling = null,
        IReadOnlyList<ContractConfig>? contracts = null,
        VerifierConfig? verifier = null)
    {
        return config.Type.ToLowerInvariant() switch
        {
            OrchestratorTypes.Sequential  => new SequentialAgentSelector(),
            OrchestratorTypes.RoundRobin  => new RoundRobinAgentSelector(),
            OrchestratorTypes.Llm         => CreateLLMSelection(config, agents),
            OrchestratorTypes.Keyword     => CreateKeywordSelection(config, agents, validationConfig, failureHandling, contracts),
            OrchestratorTypes.Structured  => CreateStructuredSelection(config, agents, failureHandling),
            OrchestratorTypes.StateMachine => CreateStateMachineSelection(config, validationConfig, failureHandling, contracts, verifier),
            OrchestratorTypes.Magentic    => throw new InvalidOperationException(
                "The 'magentic' selection type is handled by MagenticOrchestrator and should " +
                "never reach StrategyFactory. Verify that OrchestratorBuilder is routing " +
                "this config correctly."),
            _ => throw new NotSupportedException(
                $"Unknown selection strategy type: '{config.Type}'. Valid: sequential, roundrobin, llm, keyword, structured, statemachine, magentic.")
        };
    }

    private IAgentSelector CreateLLMSelection(SelectionStrategyConfig config, IReadOnlyList<AIAgent> agents)
    {
        if (config.Model is null)
            throw new InvalidOperationException("LLM selection strategy requires a 'Model' configuration.");

        var chatClient = createChatClient(config.Model);
        var prompt = config.Prompt ?? BuildDefaultSelectionPrompt();
        return new LlmAgentSelector(chatClient, prompt);
    }

    private KeywordSelectionStrategy CreateKeywordSelection(
        SelectionStrategyConfig config,
        IReadOnlyList<AIAgent> agents,
        ValidationConfig? validationConfig,
        FailureHandlingConfig? failureHandling,
        IReadOnlyList<ContractConfig>? contracts)
    {
        if (config.Routes is not { Count: > 0 })
            throw new InvalidOperationException("Keyword selection strategy requires at least one entry in 'Routes'.");

        var validators = BuildValidators(validationConfig, testSelector: _testSelector, sandboxRoot: _sandboxRoot, provenanceRegistry: _provenanceRegistry);

        // Build the contract engine once — shared across all routes that reference contracts.
        ContractEngine? contractEngine = contracts is { Count: > 0 }
            ? new ContractEngine(contracts, validationConfig, _evidenceStore, _testSelector, _sandboxRoot, _sessionId)
            : null;

        var routes = config.Routes
            .Select(r =>
            {
                var validatorNames = r.Validators is { Count: > 0 }
                    ? r.Validators
                    : (r.Validator is not null ? [r.Validator] : (IReadOnlyList<string>)[]);

                var validatorList = validatorNames
                    .Select(name =>
                    {
                        if (string.Equals(name, ValidatorNames.RequireShellPass, StringComparison.OrdinalIgnoreCase))
                            return (IRoutingValidator)new RequireShellPassValidator(
                                r.RequiredCommandPattern,
                                validationConfig?.ChangeLogPath);

                        if (string.Equals(name, ValidatorNames.RequireWriteFile, StringComparison.OrdinalIgnoreCase))
                            return (IRoutingValidator)new HandoffToTesterValidator(
                                shellFallbackPattern: r.ShellFallbackPattern,
                                testReportPath: validationConfig?.TestReportPath,
                                changeLogPath: validationConfig?.ChangeLogPath);

                        if (string.Equals(name, ValidatorNames.BlockOnConsecutiveFail, StringComparison.OrdinalIgnoreCase))
                            return (IRoutingValidator)new ConsecutiveShellFailValidator(
                                commandPattern:  r.RequiredCommandPattern,
                                changeLogPath:   validationConfig?.ChangeLogPath);

                        validators.TryGetValue(name, out var v);
                        return v;
                    })
                    .Where(v => v is not null)
                    .Select(v => v!)
                    .ToList<IRoutingValidator>();

                // Resolve named contracts into ContractValidator instances and append
                // them to the validator list (AND semantics with named validators).
                if (r.Contracts is { Count: > 0 } && contractEngine is not null)
                {
                    foreach (var contractName in r.Contracts)
                        validatorList.Add(new ContractValidator(contractEngine, contractName));
                }
                else if (r.Contracts is { Count: > 0 })
                {
                    // Contracts declared on route but no contract definitions in config — fail at
                    // startup with a clear message rather than silently skipping them.
                    var missing = string.Join(", ", r.Contracts.Select(c => $"'{c}'"));
                    throw new InvalidOperationException(
                        $"Route '{r.Keyword}' → '{r.Agent}' references contract(s) {missing} but " +
                        $"no 'Orchestration.Contracts' section is defined. Add the contract " +
                        $"definitions or remove the 'Contracts' list from the route.");
                }

                IReadOnlyList<string>? sourceAgents = r.SourceAgents is { Count: > 0 }
                    ? r.SourceAgents
                    : null;

                // Combine legacy validator names + contract names for logging/error messages.
                var allValidatorNames = validatorNames
                    .Concat(r.Contracts ?? [])
                    .ToList();

                return new KeywordSelectionStrategy.RouteEntry(
                    r.Keyword, r.Agent, validatorList, sourceAgents, allValidatorNames, r.RequireHumanApproval, r.Condition, r.RecoveryAgent, r.PreferStructuredOutput);
            })
            .ToList();

        var defaultAgent = config.DefaultAgent
            ?? (agents.Count > 0 ? agents[0].Name! : throw new InvalidOperationException("No agents defined."));

        var selectionLogger = loggerFactory?.CreateLogger<KeywordSelectionStrategy>();
        return new KeywordSelectionStrategy(routes, defaultAgent, _eventEmitter, selectionLogger, _governanceKernel, _humanApprovalService, failureHandling);
    }

    private StructuredSelectionStrategy CreateStructuredSelection(
        SelectionStrategyConfig config,
        IReadOnlyList<AIAgent> agents,
        FailureHandlingConfig? failureHandling)
    {
        if (config.StructuredRoutes is not { Count: > 0 })
            throw new InvalidOperationException(
                "Structured selection strategy requires at least one entry in 'StructuredRoutes'.");

        var routes = config.StructuredRoutes
            .Select(r => new StructuredSelectionStrategy.RouteEntry(
                AgentName:    r.Agent,
                Condition:    r.Condition,
                SourceAgents: r.SourceAgents is { Count: > 0 } ? r.SourceAgents : null))
            .ToList();

        var defaultAgent = config.DefaultAgent
            ?? (agents.Count > 0 ? agents[0].Name! : throw new InvalidOperationException("No agents defined."));

        var strategyLogger = loggerFactory?.CreateLogger<StructuredSelectionStrategy>();
        return new StructuredSelectionStrategy(routes, defaultAgent, strategyLogger, failureHandling);
    }

    private StateMachineSelectionStrategy CreateStateMachineSelection(
        SelectionStrategyConfig config,
        ValidationConfig? validationConfig,
        FailureHandlingConfig? failureHandling,
        IReadOnlyList<ContractConfig>? contracts,
        VerifierConfig? verifier = null)
    {
        if (config.StateMachine is null)
            throw new InvalidOperationException(
                "State machine selection strategy requires a 'Selection.StateMachine' configuration block.");

        var sm = config.StateMachine;

        if (string.IsNullOrWhiteSpace(sm.Initial))
            throw new InvalidOperationException(
                "Selection.StateMachine.Initial must specify the name of the starting state.");

        if (!sm.States.ContainsKey(sm.Initial))
            throw new InvalidOperationException(
                $"Selection.StateMachine.Initial state '{sm.Initial}' is not defined in States.");

        // Validate all transition targets exist.
        foreach (var (stateName, state) in sm.States)
        {
            foreach (var transition in state.Transitions)
            {
                if (!sm.States.ContainsKey(transition.To))
                    throw new InvalidOperationException(
                        $"State '{stateName}' has a transition to undefined state '{transition.To}'. " +
                        $"Add a '{transition.To}' entry to Selection.StateMachine.States.");
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
                    $"but no 'Orchestration.Contracts' section is defined.");
        }

        ContractEngine? contractEngine = contracts is { Count: > 0 }
            ? new ContractEngine(contracts, validationConfig, _evidenceStore, _testSelector, _sandboxRoot, _sessionId)
            : null;

        var strategyLogger = loggerFactory?.CreateLogger<StateMachineSelectionStrategy>();
        return new StateMachineSelectionStrategy(sm, contractEngine, failureHandling, _eventEmitter, strategyLogger, _governanceKernel, verifier, _contextAssembler);
    }

    private static Dictionary<string, IRoutingValidator> BuildValidators(
        ValidationConfig? config,
        bool isTermination = false,
        TestSelectorConfig? testSelector = null,
        string? sandboxRoot = null,
        ProvenanceRegistry? provenanceRegistry = null)
    {
        var registry = new Dictionary<string, IRoutingValidator>(StringComparer.OrdinalIgnoreCase)
        {
            [ValidatorNames.RequireWriteFile] = new HandoffToTesterValidator(testReportPath: config?.TestReportPath, changeLogPath: config?.ChangeLogPath),
            // requireCurrentTurn=true for termination validators: prevents a stale change-log
            // entry from an earlier turn satisfying the check when APPROVED fires.
            [ValidatorNames.RequireShellPass] = new RequireShellPassValidator(
                changeLogPath: config?.ChangeLogPath,
                requireCurrentTurn: isTermination,
                provenanceRegistry: provenanceRegistry),
            // Threshold defaults to 3; command pattern supplied per-route via RequiredCommandPattern.
            [ValidatorNames.BlockOnConsecutiveFail] = new ConsecutiveShellFailValidator(
                changeLogPath: config?.ChangeLogPath),
            [ValidatorNames.RequireSessionContextWrite] = new RequireSessionContextWriteValidator(),
        };

        if (config is not null)
        {
            for (int i = 0; i < config.TestAssertionPatterns.Count; i++)
            {
                var pattern = config.TestAssertionPatterns[i];
                try { _ = new Regex(pattern); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Validation.TestAssertionPatterns[{i}] ('{pattern}') is not a valid " +
                        $"regular expression: {ex.Message}", ex);
                }
            }

            registry[ValidatorNames.TestReportValid]          = new HandoffToReviewerValidator(config);
            registry[ValidatorNames.RequireBrief]             = new RequireBriefValidator(config.BriefPath);
            registry[ValidatorNames.RequireAllFilesWritten]   = new RequireAllFilesWrittenValidator(config.BriefPath, config.ChangeLogPath);
            registry[ValidatorNames.RequireReviewJudgement]   = new RequireReviewJudgementValidator();
        }

        if (testSelector is { FindRelatedCommand.Length: > 0 })
        {
            registry[ValidatorNames.RequireRelatedTestsPass] = new RequireRelatedTestsPassValidator(
                testSelector,
                changeLogPath: config?.ChangeLogPath,
                sandboxRoot: sandboxRoot,
                provenanceRegistry: provenanceRegistry);
        }

        registry[ValidatorNames.ArchitectureValidator] = new ArchitectureValidator(
            projectRoot: sandboxRoot,
            provenanceRegistry: provenanceRegistry);

        return registry;
    }

    // Termination

    public ITerminationCondition CreateTermination(
        TerminationStrategyConfig config,
        IReadOnlyList<AIAgent> agents,
        ValidationConfig? validationConfig = null)
    {
        ITerminationCondition condition = config.Type.ToLowerInvariant() switch
        {
            "regex"          => CreateRegex(config, agents),
            "structured"     => CreateStructuredTermination(config),
            "tokenbudget"    => CreateTokenBudget(config),
            "maxiterations"  => NeverTerminationCondition.Instance,
            "composite"      => CreateComposite(config, agents, validationConfig),
            _ => throw new NotSupportedException(
                $"Unknown termination strategy type: '{config.Type}'. Valid: regex, structured, tokenbudget, maxiterations, composite.")
        };

        // Wrap in validators if any are declared (maxiterations always terminates unconditionally).
        var validatorNames = config.Validators is { Count: > 0 }
            ? config.Validators
            : (config.Validator is not null ? [config.Validator] : null);

        if (validatorNames is not null && config.Type != "maxiterations")
        {
            var validatorRegistry = BuildValidators(validationConfig, isTermination: true, testSelector: _testSelector, sandboxRoot: _sandboxRoot, provenanceRegistry: _provenanceRegistry);
            var validatorList = validatorNames
                .Select(name => validatorRegistry.TryGetValue(name, out var v) ? v : null)
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();

            if (validatorList.Count > 0)
                condition = new ValidatedTerminationStrategy(condition, validatorList, _governanceKernel);
        }

        return condition;
    }

    private static RegexTerminationCondition CreateRegex(
        TerminationStrategyConfig config,
        IReadOnlyList<AIAgent> agents)
    {
        if (string.IsNullOrWhiteSpace(config.Pattern))
            throw new InvalidOperationException("Regex termination strategy requires a non-empty 'Pattern'.");

        IReadOnlyList<string>? agentNames = config.AgentNames is { Length: > 0 }
            ? config.AgentNames
            : null;

        return new RegexTerminationCondition(config.Pattern, agentNames);
    }

    private static StructuredTerminationCondition CreateStructuredTermination(
        TerminationStrategyConfig config)
    {
        if (config.Condition is null)
            throw new InvalidOperationException(
                "Structured termination strategy requires a 'Condition' block.");

        IReadOnlyList<string>? agentNames = config.AgentNames is { Length: > 0 }
            ? config.AgentNames
            : null;

        return new StructuredTerminationCondition(config.Condition, agentNames);
    }

    private static TokenBudgetTerminationCondition CreateTokenBudget(
        TerminationStrategyConfig config)
    {
        if (config.MaxTokens <= 0)
            throw new InvalidOperationException(
                "Token budget termination strategy requires a positive 'MaxTokens' value.");

        return new TokenBudgetTerminationCondition(config.MaxTokens);
    }

    private CompositeTerminationStrategy CreateComposite(
        TerminationStrategyConfig config,
        IReadOnlyList<AIAgent> agents,
        ValidationConfig? validationConfig)
    {
        if (config.Strategies is not { Count: > 0 })
            throw new InvalidOperationException("Composite termination strategy requires at least one entry in 'Strategies'.");

        var children = config.Strategies
            .Select(s => CreateTermination(s, agents, validationConfig))
            .ToList();

        return new CompositeTerminationStrategy(children);
    }

    private static string BuildDefaultSelectionPrompt() => """
        You are a group-chat moderator. Choose which agent should respond next.

        Available agents (one per line):
        {{$agents}}

        Conversation so far:
        {{$history}}

        Rules:
        - Reply with ONLY the exact agent name, nothing else.
        - Do not explain your choice.
        """;
}

