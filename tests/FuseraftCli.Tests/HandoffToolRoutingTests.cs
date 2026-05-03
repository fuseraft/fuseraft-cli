using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Strategies;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="HandoffPlugin"/> and its integration with
/// <see cref="KeywordSelectionStrategy"/>.
/// </summary>
public sealed class HandoffToolRoutingTests
{
    // -----------------------------------------------------------------------
    // HandoffPlugin — tool behaviour
    // -----------------------------------------------------------------------

    [Fact]
    public void Handoff_ReturnsRouteKeywordVerbatim()
    {
        var plugin = new HandoffPlugin();
        Assert.Equal("HANDOFF TO TESTER", plugin.Handoff("HANDOFF TO TESTER"));
        Assert.Equal("APPROVED",          plugin.Handoff("APPROVED"));
    }

    [Fact]
    public void HandoffPlugin_Constants_AreCorrect()
    {
        Assert.Equal("handoff",       HandoffPlugin.FunctionName);
        Assert.Equal("route_keyword", HandoffPlugin.ArgumentName);
    }

    // -----------------------------------------------------------------------
    // KeywordSelectionStrategy — tool-call routing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Strategy_HandoffToolCall_RoutesCorrectly()
    {
        // An assistant message that only calls handoff() — no text content.
        var toolCallMsg = AssistantWithHandoff("Developer", "HANDOFF TO TESTER");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            toolCallMsg,
        };

        var strategy = BuildStrategy("HANDOFF TO TESTER", targetAgent: "Tester", sourceAgent: null);
        strategy.SetHistory(history);
        var agents = Agents("Developer", "Tester");

        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        Assert.Equal("Tester", result?.Name);
    }

    [Fact]
    public async Task Strategy_HandoffToolCall_SourceAgentRestriction_Respected()
    {
        // Route only fires when Planner makes the call; Reviewer should be ignored.
        var toolCallMsg = AssistantWithHandoff("Reviewer", "HANDOFF TO DEVELOPER");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            toolCallMsg,
        };

        var strategy = BuildStrategy("HANDOFF TO DEVELOPER", targetAgent: "Developer", sourceAgent: "Planner");
        strategy.SetHistory(history);
        var agents = Agents("Planner", "Developer", "Reviewer");

        // Reviewer is not in SourceAgents → route should not fire → default agent returned.
        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        // Falls back to default (first) agent, not Developer.
        Assert.NotEqual("Developer", result?.Name);
    }

    [Fact]
    public async Task Strategy_HandoffToolCall_WithPassingValidator_RoutesCorrectly()
    {
        var toolCallMsg = AssistantWithHandoff("Developer", "HANDOFF TO TESTER");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            toolCallMsg,
        };

        var strategy = BuildStrategy(
            "HANDOFF TO TESTER", targetAgent: "Tester", sourceAgent: null,
            validator: new AlwaysPassValidator());
        strategy.SetHistory(history);
        var agents = Agents("Developer", "Tester");

        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        Assert.Equal("Tester", result?.Name);
    }

    [Fact]
    public async Task Strategy_HandoffToolCall_WithFailingValidator_ReturnsSourceAgent()
    {
        var toolCallMsg = AssistantWithHandoff("Developer", "HANDOFF TO TESTER");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            toolCallMsg,
        };

        var strategy = BuildStrategy(
            "HANDOFF TO TESTER", targetAgent: "Tester", sourceAgent: null,
            validator: new AlwaysFailValidator("tests not passing"));
        strategy.SetHistory(history);
        var agents = Agents("Developer", "Tester");

        // Validator blocks the route → source agent (Developer) is returned.
        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        Assert.Equal("Developer", result?.Name);
    }

    [Fact]
    public async Task Strategy_TextKeyword_StillWorksWithoutToolCall()
    {
        // Backward-compat: plain text keyword still routes correctly.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            new(ChatRole.Assistant, "Work complete.\n\nHANDOFF TO TESTER") { AuthorName = "Developer" },
        };

        var strategy = BuildStrategy("HANDOFF TO TESTER", targetAgent: "Tester", sourceAgent: null);
        strategy.SetHistory(history);
        var agents = Agents("Developer", "Tester");

        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        Assert.Equal("Tester", result?.Name);
    }

    [Fact]
    public async Task Strategy_UnknownToolKeyword_FallsThrough_NoMatch()
    {
        // Agent calls handoff with a keyword that isn't in any route → no route fires.
        var toolCallMsg = AssistantWithHandoff("Developer", "UNKNOWN KEYWORD XYZ");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            toolCallMsg,
        };

        var strategy = BuildStrategy("HANDOFF TO TESTER", targetAgent: "Tester", sourceAgent: null);
        strategy.SetHistory(history);
        var agents = Agents("Developer", "Tester");

        // No match → default agent (Developer) returned.
        var result = await strategy.SelectAsync(agents, history, CancellationToken.None);

        Assert.NotEqual("Tester", result?.Name);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ChatMessage AssistantWithHandoff(string authorName, string routeKeyword)
    {
        var msg = new ChatMessage(ChatRole.Assistant, (string?)null);
        msg.Contents = [new FunctionCallContent(
            "call-1",
            HandoffPlugin.FunctionName,
            new Dictionary<string, object?> { [HandoffPlugin.ArgumentName] = routeKeyword })];
        msg.AuthorName = authorName;
        return msg;
    }

    private static KeywordSelectionStrategy BuildStrategy(
        string keyword,
        string targetAgent,
        string? sourceAgent,
        IRoutingValidator? validator = null)
    {
        IReadOnlyList<string>? sourceAgents = sourceAgent is not null ? [sourceAgent] : null;
        IReadOnlyList<IRoutingValidator> validators = validator is not null ? [validator] : [];

        var routes = new[]
        {
            new KeywordSelectionStrategy.RouteEntry(
                Keyword:      keyword,
                AgentName:    targetAgent,
                Validators:   validators,
                SourceAgents: sourceAgents),
        };

        // Default agent is the first agent name in the route's source or a fallback.
        return new KeywordSelectionStrategy(routes, defaultAgentName: sourceAgent ?? targetAgent);
    }

    private static IReadOnlyList<AIAgent> Agents(params string[] names) =>
        names.Select(n => new ChatClientAgent(new StubChatClient(), null, n, null, null))
             .Cast<AIAgent>()
             .ToList();

    private sealed class AlwaysPassValidator : IRoutingValidator
    {
        public Task<RoutingValidationResult> ValidateAsync(
            IList<ChatMessage> history, CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingValidationResult.Pass());
    }

    private sealed class AlwaysFailValidator(string error = "blocked") : IRoutingValidator
    {
        public Task<RoutingValidationResult> ValidateAsync(
            IList<ChatMessage> history, CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingValidationResult.Fail(error));
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyAsync();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
