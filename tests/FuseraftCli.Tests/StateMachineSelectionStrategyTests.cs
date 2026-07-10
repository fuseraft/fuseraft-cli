using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Exceptions;
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
/// threshold regardless of action, matching <c>KeywordSelectionStrategy</c>.
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
}
