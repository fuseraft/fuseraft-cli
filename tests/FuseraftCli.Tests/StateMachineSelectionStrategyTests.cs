using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Contracts;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Orchestration;
using fuseraft.Orchestration.Strategies;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression coverage for <see cref="StateMachineSelectionStrategy"/>'s threshold-based
/// escalation. It used to only check <c>FailureAction.Abort</c>
/// (<c>if (typeConfig.Action == FailureAction.Abort &amp;&amp; newCount >= typeConfig.Threshold)</c>),
/// silently making <c>Threshold</c> dead for every failure type that defaults to
/// <c>Reinstruct</c> (<c>MissingEvidence</c>, <c>InvalidTransition</c>, <c>ConflictingEvidence</c>
/// all default to <c>Reinstruct</c> with a non-zero <c>Threshold</c>). Now it checks the
/// threshold regardless of action.
/// </summary>
public sealed class StateMachineSelectionStrategyTests
{
    private static StateMachineSelectionStrategy NewStrategy(FailureHandlingConfig? failureHandling = null)
    {
        var machine = new StateMachineConfig
        {
            Initial = "Implementation",
            States = new Dictionary<string, StateConfig>
            {
                ["Implementation"] = new StateConfig
                {
                    Agent = "Developer",
                    Transitions =
                    [
                        new TransitionConfig { To = "Testing", Signal = "HANDOFF TO TESTER", Contract = "ImplementationComplete" },
                    ],
                },
                ["Testing"] = new StateConfig { Agent = "Tester" },
            },
        };

        return new StateMachineSelectionStrategy(machine, failureHandling: failureHandling);
    }

    private static (StateConfig State, TransitionConfig Transition) ImplementationToTesting()
    {
        var state = new StateConfig
        {
            Agent = "Developer",
            Transitions = [new TransitionConfig { To = "Testing", Signal = "HANDOFF TO TESTER", Contract = "ImplementationComplete" }],
        };
        return (state, state.Transitions[0]);
    }

    [Fact]
    public async Task ReinstructAction_EscalatesOnceThresholdReached_NotOnlyForAbort()
    {
        // InvalidTransition defaults to Reinstruct with Threshold=3. Force Threshold=1 so a
        // single failure must escalate — proving Reinstruct is no longer silently exempt.
        var strategy = NewStrategy(new FailureHandlingConfig
        {
            InvalidTransition = new FailureTypeConfig { Action = FailureAction.Reinstruct, Threshold = 1 },
        });
        var (state, transition) = ImplementationToTesting();

        // "prerequisite not met" matches no MissingEvidence/ConflictingEvidence marker, so
        // FailureClassifier falls through to InvalidTransition.
        await Assert.ThrowsAsync<ValidatorStuckException>(() =>
            strategy.HandleTransitionFailureAsync(
                state, transition, failingContract: "ImplementationComplete",
                errorMessage: "prerequisite not met", agents: [], history: [],
                authorName: "Developer", cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ReinstructAction_BelowThreshold_DoesNotEscalate()
    {
        // Default InvalidTransition.Threshold is 3 — a single failure must not throw.
        var strategy = NewStrategy();
        var (state, transition) = ImplementationToTesting();
        strategy.SetHistory(new List<ChatMessage>());

        var recovery = await strategy.HandleTransitionFailureAsync(
            state, transition, failingContract: "ImplementationComplete",
            errorMessage: "prerequisite not met", agents: [], history: [],
            authorName: "Developer", cancellationToken: CancellationToken.None);

        Assert.Null(recovery); // re-invoke current agent, not escalate
    }

    [Fact]
    public async Task EscalateToHumanAction_ThrowsImmediately_RegardlessOfThreshold()
    {
        var strategy = NewStrategy(new FailureHandlingConfig
        {
            InvalidTransition = new FailureTypeConfig { Action = FailureAction.EscalateToHuman, Threshold = 10 },
        });
        var (state, transition) = ImplementationToTesting();

        await Assert.ThrowsAsync<ValidatorStuckException>(() =>
            strategy.HandleTransitionFailureAsync(
                state, transition, failingContract: "ImplementationComplete",
                errorMessage: "prerequisite not met", agents: [], history: [],
                authorName: "Developer", cancellationToken: CancellationToken.None));
    }

    // ── Approve and parallel gates ───────────────────────────────────────────

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Operator(bool approve) : IHumanApprovalService
    {
        public List<(string Route, string From, string To)> Asked { get; } = [];
        public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent)
        {
            Asked.Add((keyword, sourceAgent, targetAgent));
            return Task.FromResult(approve);
        }
        public Task<string?> PromptContinueAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptRedirectAsync(string agentName) => Task.FromResult<string?>(null);
        public Task<string?> PromptValidatorStuckAsync(string agentName, string validatorName, int consecutiveFailures, string lastError) => Task.FromResult<string?>(null);
        public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage) => Task.FromResult<string?>(null);
        public Task<string?> PromptPostSessionAsync() => Task.FromResult<string?>(null);
        public Task<bool> PromptShellCommandAsync(string command) => Task.FromResult(true);
        public Task<bool> PromptToolActionAsync(string plugin, string action, string detail) => Task.FromResult(true);
        public Task<bool> PromptFileWriteAsync(string action, string path, string oldContent, string newContent) => Task.FromResult(true);
        public Task<string?> PromptPlanReviewAsync(string planText) => Task.FromResult<string?>(null);
    }

    private static AIAgent Agent(string name) => new ChatClientAgent(new NoopChatClient(), instructions: "x", name: name);

    private static ChatMessage Handoff(string from, string route) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"c-{Guid.NewGuid():N}", HandoffPlugin.FunctionName,
            new Dictionary<string, object?> { [HandoffPlugin.ArgumentName] = route })]) { AuthorName = from };

    private static StateMachineSelectionStrategy Machine(TransitionConfig transition, IHumanApprovalService? op, IEnumerable<ContractConfig>? contracts = null)
    {
        var states = new Dictionary<string, StateConfig>
        {
            ["Lead"] = new StateConfig { Agent = "Lead", Transitions = [transition] },
            ["end"]  = new StateConfig { Agent = string.Empty, Terminal = true },
        };
        foreach (var name in new[] { "A", "B", "Join" })
            states[name] = new StateConfig { Agent = name, Transitions = [new TransitionConfig { To = "end", Signal = "end" }] };
        var engine = contracts is null ? null : new ContractEngine(contracts);
        return new StateMachineSelectionStrategy(new StateMachineConfig { Initial = "Lead", States = states },
            contractEngine: engine, humanApprovalService: op);
    }

    private static readonly IReadOnlyList<AIAgent> Pool = [Agent("Lead"), Agent("A"), Agent("B"), Agent("Join")];

    [Fact]
    public async Task Approve_Approved_TakesTheEdge()
    {
        var op = new Operator(approve: true);
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end", RequireHumanApproval = true }, op);
        var history = new List<ChatMessage> { Handoff("Lead", "end") };
        sm.SetHistory(history);

        Assert.Null(await sm.SelectAsync(Pool, history));
        Assert.Equal([("end", "Lead", "end")], op.Asked);
    }

    [Fact]
    public async Task Approve_Refused_SendsWorkBack_AndDoesNotAskAgainForTheSameHandoff()
    {
        var op = new Operator(approve: false);
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end", RequireHumanApproval = true }, op);
        var history = new List<ChatMessage> { Handoff("Lead", "end") };
        sm.SetHistory(history);

        Assert.Equal("Lead", (await sm.SelectAsync(Pool, history))!.Name);
        Assert.Contains(history, m => m.Text.Contains("didn't approve the handoff to end"));
        Assert.Contains(history, m => m.Text == "[fuseraft:blocked Lead→end]");

        history.Add(new ChatMessage(ChatRole.Assistant, "Still working.") { AuthorName = "Lead" });
        Assert.Equal("Lead", (await sm.SelectAsync(Pool, history))!.Name);
        Assert.Single(op.Asked);
    }

    [Fact]
    public async Task Approve_NotRequested_NeverAsks()
    {
        var op = new Operator(approve: false);
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end" }, op);
        var history = new List<ChatMessage> { Handoff("Lead", "end") };
        sm.SetHistory(history);

        Assert.Null(await sm.SelectAsync(Pool, history));
        Assert.Empty(op.Asked);
    }

    private static TransitionConfig ParallelEdge(bool approve = false, string? contract = null) => new()
    {
        To = "Join", Targets = ["A", "B"], Parallel = true, Signal = "A+B", Contract = contract, RequireHumanApproval = approve,
    };

    private static ContractConfig FileContract(string path) =>
        new() { Name = "ready", Requires = [new ContractPredicate { Type = "FileExists", Path = path }] };

    [Fact]
    public async Task Parallel_GateFails_DoesNotFanOut()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        var sm = Machine(ParallelEdge(contract: "ready"), null, [FileContract(missing)]);
        var history = new List<ChatMessage> { Handoff("Lead", "A+B") };
        sm.SetHistory(history);

        Assert.Null(await sm.TrySelectParallelAsync(Pool, history));
        Assert.Equal("Lead", (await sm.SelectAsync(Pool, history))!.Name);
        Assert.Contains(history, m => m.Text == "[fuseraft:blocked Lead→Join]");
    }

    [Fact]
    public async Task Parallel_GatePasses_FansOut()
    {
        var file = Path.GetTempFileName();
        try
        {
            var sm = Machine(ParallelEdge(contract: "ready"), null, [FileContract(file)]);
            var history = new List<ChatMessage> { Handoff("Lead", "A+B") };
            sm.SetHistory(history);

            var batch = await sm.TrySelectParallelAsync(Pool, history);
            Assert.Equal(["A", "B"], batch!.Branches.Select(b => b.Agent.Name));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Parallel_Refused_ReinvokesTheStepAgent()
    {
        var op = new Operator(approve: false);
        var sm = Machine(ParallelEdge(approve: true), op);
        var history = new List<ChatMessage> { Handoff("Lead", "A+B") };
        sm.SetHistory(history);

        Assert.Null(await sm.TrySelectParallelAsync(Pool, history));
        Assert.Equal([("A+B", "Lead", "A, B")], op.Asked);
        Assert.Equal("Lead", (await sm.SelectAsync(Pool, history))!.Name);
        Assert.Equal("Lead", sm.CurrentState);
        Assert.Single(op.Asked);
    }

    // ── Corrections reach agents that see only their handoff ─────────────────

    // A handoff agent is shown only the corrections ContextWindowFilter recognises, so every
    // message the strategy injects (other than turn markers) must be one of them.
    private static void AssertEveryInjectionIsACorrection(IEnumerable<ChatMessage> injected)
    {
        var messages = injected.Where(m => m.Role == ChatRole.User && !m.Text.StartsWith("[fuseraft", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.True(
            fuseraft.Orchestration.Context.ContextWindowFilter.IsCorrectionMessage(m), $"Not recognised as a correction: {m.Text}"));
    }

    [Fact]
    public async Task GateFailure_FirstAndRepeated_AreRecognisedCorrections()
    {
        var strategy = NewStrategy();
        var history = new List<ChatMessage>();
        strategy.SetHistory(history);
        var (state, transition) = ImplementationToTesting();

        for (var i = 0; i < 2; i++)
            await strategy.HandleTransitionFailureAsync(state, transition, "ImplementationComplete",
                "prerequisite not met", [], history, "Developer", CancellationToken.None);

        AssertEveryInjectionIsACorrection(history);
    }

    [Fact]
    public async Task ApprovalRefusal_IsARecognisedCorrection()
    {
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end", RequireHumanApproval = true }, new Operator(approve: false));
        var history = new List<ChatMessage> { Handoff("Lead", "end") };
        sm.SetHistory(history);

        await sm.SelectAsync(Pool, history);

        AssertEveryInjectionIsACorrection(history.Skip(1));
    }

    [Fact]
    public async Task MissingHandoff_IsARecognisedCorrection()
    {
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end" }, null);
        var history = new List<ChatMessage> { new(ChatRole.Assistant, "I think I'm done.") { AuthorName = "Lead" } };
        sm.SetHistory(history);

        await sm.SelectAsync(Pool, history);

        AssertEveryInjectionIsACorrection(history.Skip(1));
    }

    [Fact]
    public async Task HandoffToAnUnknownRoute_IsARecognisedCorrection()
    {
        var sm = Machine(new TransitionConfig { To = "end", Signal = "end" }, null);
        var history = new List<ChatMessage> { Handoff("Lead", "Elsewhere") };
        sm.SetHistory(history);

        await sm.SelectAsync(Pool, history);

        AssertEveryInjectionIsACorrection(history.Skip(1));
    }
}
