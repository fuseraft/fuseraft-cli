using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Strategies;

namespace FuseraftCli.Tests;

public sealed class StrategyFactoryTests
{
    private readonly StrategyFactory _factory = new(new ChatClientFactory().Create);

    private static readonly IReadOnlyList<AIAgent> NoAgents = [];

    // Selection strategies

    [Theory]
    [InlineData("sequential")]
    [InlineData("roundrobin")]
    [InlineData("SEQUENTIAL")]   // case-insensitive
    public void CreateSelection_Sequential_ReturnsIAgentSelector(string type)
    {
        var config = new SelectionStrategyConfig { Type = type };

        var strategy = _factory.CreateSelection(config, NoAgents);

        Assert.IsAssignableFrom<IAgentSelector>(strategy);
    }

    [Fact]
    public void CreateSelection_UnknownType_ThrowsNotSupportedException()
    {
        var config = new SelectionStrategyConfig { Type = "banana" };

        Assert.Throws<NotSupportedException>(
            () => _factory.CreateSelection(config, NoAgents));
    }

    // Termination strategies

    [Fact]
    public async Task CreateTermination_MaxIterations_ReturnsNonTerminatingCondition()
    {
        var config = new TerminationStrategyConfig { Type = "maxiterations", MaxIterations = 5 };

        var condition = _factory.CreateTermination(config, NoAgents);

        Assert.IsAssignableFrom<ITerminationCondition>(condition);
        // MaxIterations is handled by the orchestrator loop — the condition itself never terminates.
        var shouldTerminate = await condition.ShouldTerminateAsync([]);
        Assert.False(shouldTerminate);
    }

    [Fact]
    public async Task CreateTermination_Regex_TerminatesWhenPatternMatches()
    {
        var config = new TerminationStrategyConfig
        {
            Type        = "regex",
            Pattern     = "DONE",
            MaxIterations = 10
        };

        var condition = _factory.CreateTermination(config, NoAgents);

        Assert.IsAssignableFrom<ITerminationCondition>(condition);
        var msg = new ChatMessage(ChatRole.Assistant, "Task DONE");
        var shouldTerminate = await condition.ShouldTerminateAsync([msg]);
        Assert.True(shouldTerminate);
    }

    [Fact]
    public void CreateTermination_Regex_ThrowsWhenPatternIsEmpty()
    {
        var config = new TerminationStrategyConfig { Type = "regex", Pattern = "" };

        Assert.Throws<InvalidOperationException>(
            () => _factory.CreateTermination(config, NoAgents));
    }

    [Fact]
    public async Task CreateTermination_Structured_TerminatesWhenConditionMatches()
    {
        var config = new TerminationStrategyConfig
        {
            Type          = "structured",
            Condition     = new StructuredCondition { Field = "status", Is = "done" },
            MaxIterations = 10
        };

        var condition = _factory.CreateTermination(config, NoAgents);

        Assert.IsAssignableFrom<ITerminationCondition>(condition);
        var msg = new ChatMessage(ChatRole.Assistant, "{\"status\": \"done\"}");
        var shouldTerminate = await condition.ShouldTerminateAsync([msg]);
        Assert.True(shouldTerminate);
    }

    [Fact]
    public async Task CreateTermination_Structured_DoesNotTerminateWhenConditionDoesNotMatch()
    {
        var config = new TerminationStrategyConfig
        {
            Type      = "structured",
            Condition = new StructuredCondition { Field = "status", Is = "done" }
        };

        var condition = _factory.CreateTermination(config, NoAgents);

        var msg = new ChatMessage(ChatRole.Assistant, "{\"status\": \"in_progress\"}");
        var shouldTerminate = await condition.ShouldTerminateAsync([msg]);
        Assert.False(shouldTerminate);
    }

    [Fact]
    public void CreateTermination_Structured_ThrowsWhenConditionIsMissing()
    {
        var config = new TerminationStrategyConfig { Type = "structured" };

        Assert.Throws<InvalidOperationException>(
            () => _factory.CreateTermination(config, NoAgents));
    }

    [Fact]
    public void CreateTermination_TokenBudget_ThrowsWhenMaxTokensIsZero()
    {
        var config = new TerminationStrategyConfig { Type = "tokenbudget", MaxTokens = 0 };

        Assert.Throws<InvalidOperationException>(
            () => _factory.CreateTermination(config, NoAgents));
    }

    [Fact]
    public async Task CreateTermination_TokenBudget_NeverTerminatesBeforeReaderIsWired()
    {
        var config = new TerminationStrategyConfig { Type = "tokenbudget", MaxTokens = 100 };

        var condition = _factory.CreateTermination(config, NoAgents);

        Assert.IsType<TokenBudgetTerminationCondition>(condition);
        var shouldTerminate = await condition.ShouldTerminateAsync([]);
        Assert.False(shouldTerminate);
    }

    [Fact]
    public async Task CreateTermination_TokenBudget_TerminatesOnceWiredReaderReachesThreshold()
    {
        var config = new TerminationStrategyConfig { Type = "tokenbudget", MaxTokens = 100 };
        var condition = Assert.IsType<TokenBudgetTerminationCondition>(
            _factory.CreateTermination(config, NoAgents));

        int tokens = 50;
        condition.SetTokenReader(() => tokens);
        Assert.False(await condition.ShouldTerminateAsync([]));

        tokens = 100;
        Assert.True(await condition.ShouldTerminateAsync([]));
    }

    [Fact]
    public void CreateTermination_TokenBudget_WithValidator_WrapsAndExposesInnerCondition()
    {
        // A tokenbudget node that also declares a Validator gets wrapped in
        // ValidatedTerminationStrategy — Inner must expose the wrapped condition so
        // AgentOrchestrator.WireTokenBudget can still reach it and wire the token reader.
        var config = new TerminationStrategyConfig
        {
            Type      = "tokenbudget",
            MaxTokens = 100,
            Validator = ValidatorNames.RequireShellPass
        };

        var condition = Assert.IsType<ValidatedTerminationStrategy>(
            _factory.CreateTermination(config, NoAgents, new ValidationConfig()));

        Assert.IsType<TokenBudgetTerminationCondition>(condition.Inner);
    }

    [Fact]
    public void CreateTermination_Composite_RequiresAtLeastOneChild()
    {
        var config = new TerminationStrategyConfig
        {
            Type = "composite",
            Strategies = []
        };

        Assert.Throws<InvalidOperationException>(
            () => _factory.CreateTermination(config, NoAgents));
    }

    [Fact]
    public void CreateTermination_Composite_ReturnsCompositeStrategy()
    {
        var config = new TerminationStrategyConfig
        {
            Type = "composite",
            Strategies =
            [
                new TerminationStrategyConfig { Type = "maxiterations", MaxIterations = 5 },
                new TerminationStrategyConfig { Type = "regex", Pattern = "DONE" }
            ]
        };

        var strategy = _factory.CreateTermination(config, NoAgents);

        Assert.IsType<CompositeTerminationStrategy>(strategy);
    }

    [Fact]
    public void CreateTermination_UnknownType_ThrowsNotSupportedException()
    {
        var config = new TerminationStrategyConfig { Type = "unknown" };

        Assert.Throws<NotSupportedException>(
            () => _factory.CreateTermination(config, NoAgents));
    }

    // Validation config — startup checks

    [Fact]
    public void CreateSelection_Keyword_InvalidTestAssertionPattern_ThrowsAtStartup()
    {
        // Bad regex in TestAssertionPatterns must be caught at config-load time,
        // not silently dropped at runtime (which would disable fake-test detection).
        var validationConfig = new ValidationConfig
        {
            TestAssertionPatterns = [@"\bassert\b", "[invalid_regex"]
        };
        var config = new SelectionStrategyConfig
        {
            Type         = "keyword",
            DefaultAgent = "Agent",
            Routes       = [new KeywordRoute { Keyword = "DONE", Agent = "Agent" }]
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => _factory.CreateSelection(config, NoAgents, validationConfig));

        Assert.Contains("TestAssertionPatterns[1]", ex.Message);
        Assert.Contains("[invalid_regex", ex.Message);
    }

    [Fact]
    public void CreateSelection_Keyword_ValidTestAssertionPatterns_DoesNotThrow()
    {
        var validationConfig = new ValidationConfig
        {
            TestAssertionPatterns = [@"\bassert\b", @"\bexpect\b", @"assertEqual"]
        };
        var config = new SelectionStrategyConfig
        {
            Type         = "keyword",
            DefaultAgent = "Agent",
            Routes       = [new KeywordRoute { Keyword = "DONE", Agent = "Agent" }]
        };

        // Should not throw — all patterns are valid regexes.
        var strategy = _factory.CreateSelection(config, NoAgents, validationConfig);

        Assert.IsType<KeywordSelectionStrategy>(strategy);
    }
}
