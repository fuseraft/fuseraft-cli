using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Strategies;

namespace FuseraftCli.Tests;

public sealed class StrategyFactoryTests
{
    private readonly StrategyFactory _factory = new();

    private static SelectionStrategyConfig Steps(string initial = "A", params (string Name, string? To)[] states) => new()
    {
        Type = OrchestratorTypes.StateMachine,
        StateMachine = new StateMachineConfig
        {
            Initial = initial,
            States = states.ToDictionary(
                s => s.Name,
                s => new StateConfig
                {
                    Agent       = s.Name,
                    Transitions = s.To is null ? [] : [new TransitionConfig { To = s.To, Signal = s.To }],
                }),
        },
    };

    [Fact]
    public void CreateSelection_StepsFlow_ReturnsStateMachine()
    {
        var selector = _factory.CreateSelection(Steps("A", ("A", "B"), ("B", null)));
        Assert.IsType<StateMachineSelectionStrategy>(selector);
    }

    [Theory]
    [InlineData("magentic")]
    [InlineData("mapreduce")]
    [InlineData("keyword")]
    public void CreateSelection_OtherFlowTypes_AreNotSupported(string type) =>
        Assert.Throws<NotSupportedException>(() => _factory.CreateSelection(new SelectionStrategyConfig { Type = type }));

    [Fact]
    public void CreateSelection_TransitionToUndefinedState_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _factory.CreateSelection(Steps("A", ("A", "Ghost"))));
        Assert.Contains("undefined state 'Ghost'", ex.Message);
    }

    [Fact]
    public void CreateSelection_ContractWithoutDefinition_Throws()
    {
        var config = Steps("A", ("A", "B"), ("B", null));
        var sm = config.StateMachine!;
        config = config with
        {
            StateMachine = sm with
            {
                States = new Dictionary<string, StateConfig>(sm.States)
                {
                    ["A"] = sm.States["A"] with { Transitions = [new TransitionConfig { To = "B", Signal = "B", Contract = "brief" }] },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => _factory.CreateSelection(config));
        Assert.Contains("'brief'", ex.Message);
    }
}
