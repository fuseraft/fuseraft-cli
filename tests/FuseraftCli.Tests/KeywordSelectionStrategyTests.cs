using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Strategies;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="KeywordSelectionStrategy"/> focused on the
/// stuck-validator HITL escalation path.
/// </summary>
public sealed class KeywordSelectionStrategyTests
{
    // Builds a ChatClientAgent with the given name using a no-op stub chat client.
    // No network calls are ever made.
    private static ChatClientAgent MakeAgent(string name)
        => new(new StubChatClient(), instructions: null, name: name, description: null, tools: null);

    // Calls the public SelectAsync method on the strategy.
    private static Task<AIAgent?> SelectAsync(
        KeywordSelectionStrategy strategy,
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history)
        => strategy.SelectAsync(agents, history, CancellationToken.None);

    // A validator that always fails with a fixed error message.
    private sealed class AlwaysFailValidator(string error = "blocked") : IRoutingValidator
    {
        public Task<RoutingValidationResult> ValidateAsync(
            IList<ChatMessage> history,
            CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingValidationResult.Fail(error));
    }

    // A validator that always passes.
    private sealed class AlwaysPassValidator : IRoutingValidator
    {
        public Task<RoutingValidationResult> ValidateAsync(
            IList<ChatMessage> history,
            CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingValidationResult.Pass());
    }

    // Builds a one-route strategy where the Developer can hand off to Tester,
    // guarded by the given validator.
    private static KeywordSelectionStrategy BuildStrategy(
        IRoutingValidator validator,
        string validatorName = "TestValidator")
    {
        var routes = new[]
        {
            new KeywordSelectionStrategy.RouteEntry(
                Keyword:        "HANDOFF TO TESTER",
                AgentName:      "Tester",
                Validators:     [validator],
                SourceAgents:   ["Developer"],
                ValidatorNames: [validatorName])
        };
        return new KeywordSelectionStrategy(routes, defaultAgentName: "Developer");
    }

    // History: User message → Developer message containing the handoff keyword.
    private static IList<ChatMessage> HandoffHistory() =>
    [
        new ChatMessage(ChatRole.User, "task"),
        new ChatMessage(ChatRole.Assistant, "HANDOFF TO TESTER") { AuthorName = "Developer" }
    ];

    // -------------------------------------------------------------------------
    // Threshold escalation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidatorFails_BelowThreshold_ReturnsDeveloper_NoException()
    {
        var strategy = BuildStrategy(new AlwaysFailValidator());
        var agents   = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };
        var history  = HandoffHistory();

        // Two consecutive failures — below threshold of 3.
        var r1 = await SelectAsync(strategy, agents, history);
        var r2 = await SelectAsync(strategy, agents, history);

        Assert.Equal("Developer", r1?.Name);
        Assert.Equal("Developer", r2?.Name);
    }

    [Fact]
    public async Task ValidatorFails_AtThreshold_ThrowsValidatorStuckException()
    {
        var strategy = BuildStrategy(new AlwaysFailValidator("validation error text"), "MyValidator");
        var agents   = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };
        var history  = HandoffHistory();

        await SelectAsync(strategy, agents, history); // failure 1
        await SelectAsync(strategy, agents, history); // failure 2

        var ex = await Assert.ThrowsAsync<ValidatorStuckException>(
            () => SelectAsync(strategy, agents, history)); // failure 3 — throws

        Assert.Equal("Developer",         ex.AgentName);
        Assert.Equal("MyValidator",       ex.ValidatorName);
        Assert.Equal(3,                   ex.ConsecutiveFailures);
        Assert.Equal("validation error text", ex.LastValidatorError);
    }

    [Fact]
    public async Task ValidatorFails_PastThreshold_CounterReset_DoesNotThrowAgain()
    {
        // After throwing, the counter is reset so the next call starts fresh.
        var strategy = BuildStrategy(new AlwaysFailValidator());
        var agents   = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };
        var history  = HandoffHistory();

        await SelectAsync(strategy, agents, history); // 1
        await SelectAsync(strategy, agents, history); // 2
        await Assert.ThrowsAsync<ValidatorStuckException>(
            () => SelectAsync(strategy, agents, history)); // 3 — throws, resets

        // Next call starts a fresh counter — should not throw.
        var result = await SelectAsync(strategy, agents, history); // 1 again
        Assert.Equal("Developer", result?.Name);
    }

    // -------------------------------------------------------------------------
    // Counter reset on validator pass
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidatorPassAfterFailures_ResetsCounter_DoesNotThrow()
    {
        int calls = 0;
        var flippingValidator = new DelegateValidator(_ =>
        {
            calls++;
            return calls < 3
                ? RoutingValidationResult.Fail("blocked")
                : RoutingValidationResult.Pass();
        });

        var strategy = new KeywordSelectionStrategy(
        [
            new KeywordSelectionStrategy.RouteEntry(
                "HANDOFF TO TESTER", "Tester", [flippingValidator], ["Developer"], ["Flipping"])
        ], "Developer");

        var agents  = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };
        var history = HandoffHistory();

        var r1 = await SelectAsync(strategy, agents, history); // fail 1 → Developer
        var r2 = await SelectAsync(strategy, agents, history); // fail 2 → Developer
        var r3 = await SelectAsync(strategy, agents, history); // pass  → Tester (counter cleared)

        Assert.Equal("Developer", r1?.Name);
        Assert.Equal("Developer", r2?.Name);
        Assert.Equal("Tester",    r3?.Name);
    }

    // -------------------------------------------------------------------------
    // ValidatorStuckException properties
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatorStuckException_Properties_AreCorrect()
    {
        var ex = new ValidatorStuckException(
            agentName:           "Developer",
            validatorName:       "RequireBrief",
            consecutiveFailures: 3,
            lastValidatorError:  "brief.json not found");

        Assert.Equal("Developer",           ex.AgentName);
        Assert.Equal("RequireBrief",         ex.ValidatorName);
        Assert.Equal(3,                      ex.ConsecutiveFailures);
        Assert.Equal("brief.json not found", ex.LastValidatorError);
        Assert.Contains("Developer",         ex.Message);
        Assert.Contains("RequireBrief",      ex.Message);
    }

    // -------------------------------------------------------------------------
    // Retry prefix injection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidatorFails_FirstAttempt_NoRetryPrefix()
    {
        var history  = HandoffHistory();
        var injected = new List<ChatMessage>();
        var strategy = BuildStrategy(new AlwaysFailValidator("the real error"));
        strategy.SetHistory(injected);

        var agents = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };

        await SelectAsync(strategy, agents, history);

        // Only the validator error message should be injected — no RETRY prefix.
        var errorMsg = injected.Last(m => m.Role == ChatRole.User).Text ?? "";
        Assert.DoesNotContain("RETRY", errorMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the real error", errorMsg);
    }

    [Fact]
    public async Task ValidatorFails_SecondAttempt_InjectsEscalatedMessage()
    {
        // When SetHistory is active, the second failure detects no tool calls were made
        // (the test history has no Tool messages) and classifies it as NoProgress.
        // The NoProgress injection is a "NO TOOL CALLS:" message rather than
        // the generic "RETRY N/M" prefix used for InvalidTransition failures.
        var history  = HandoffHistory();
        var injected = new List<ChatMessage>();
        var strategy = BuildStrategy(new AlwaysFailValidator("the real error"));
        strategy.SetHistory(injected);

        var agents = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };

        await SelectAsync(strategy, agents, history); // failure 1
        await SelectAsync(strategy, agents, history); // failure 2 — NoProgress detected

        var messages = injected.Where(m => m.Role == ChatRole.User).ToList();
        var secondError = messages.Last().Text ?? "";

        // NoProgress: agent had a prior injection but no tool calls → "NO TOOL CALLS" message.
        Assert.Contains("NO TOOL CALLS", secondError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the real error", secondError);
    }

    [Fact]
    public async Task ValidatorFails_FirstAttemptMessage_DoesNotContainRetryCountFromPreviousRun()
    {
        // After the counter resets (following an exception), the next first-failure
        // message must not carry over a stale retry count.
        var history  = HandoffHistory();
        var injected = new List<ChatMessage>();
        var strategy = BuildStrategy(new AlwaysFailValidator("blocked"));
        strategy.SetHistory(injected);

        var agents = new AIAgent[] { MakeAgent("Developer"), MakeAgent("Tester") };

        await SelectAsync(strategy, agents, history); // 1
        await SelectAsync(strategy, agents, history); // 2
        await Assert.ThrowsAsync<ValidatorStuckException>(
            () => SelectAsync(strategy, agents, history)); // 3 — resets

        injected.Clear(); // start fresh observation
        await SelectAsync(strategy, agents, history); // 1 again after reset

        var msg = injected.Last(m => m.Role == ChatRole.User).Text ?? "";
        Assert.DoesNotContain("RETRY", msg, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Missing-keyword re-invoke (non-default agent finished with empty text)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoKeyword_LastAgentIsNonDefault_ReInvokesLastAgent()
    {
        // Developer (non-default) finishes its turn with empty final text — no keyword.
        // The strategy must re-invoke Developer, not fall back to Planner (default).
        var routes = Array.Empty<KeywordSelectionStrategy.RouteEntry>();
        var strategy = new KeywordSelectionStrategy(routes, defaultAgentName: "Planner");

        var agents = new[] { MakeAgent("Planner"), MakeAgent("Developer") };
        var history = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "task"),
            // Developer made tool calls then ended with empty text
            new ChatMessage(ChatRole.Assistant, "") { AuthorName = "Developer" },
            new ChatMessage(ChatRole.Tool,  "tool result"),
            new ChatMessage(ChatRole.Assistant, "") { AuthorName = "Developer" }, // empty final
        };

        var injected = new List<ChatMessage>();
        strategy.SetHistory(injected);

        var selected = await SelectAsync(strategy, agents, history);

        Assert.Equal("Developer", selected?.Name);
        Assert.Contains(injected, m =>
            m.Role == ChatRole.User &&
            (m.Text ?? "").Contains("handoff keyword", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoKeyword_LastAgentIsDefault_FallsBackToDefault()
    {
        // When the last active agent IS the default, normal fallback applies — no re-invoke.
        var routes = Array.Empty<KeywordSelectionStrategy.RouteEntry>();
        var strategy = new KeywordSelectionStrategy(routes, defaultAgentName: "Planner");

        var agents = new[] { MakeAgent("Planner"), MakeAgent("Developer") };
        var history = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "task"),
            new ChatMessage(ChatRole.Assistant, "Thinking...") { AuthorName = "Planner" },
        };

        var injected = new List<ChatMessage>();
        strategy.SetHistory(injected);

        var selected = await SelectAsync(strategy, agents, history);

        Assert.Equal("Planner", selected?.Name);
        Assert.DoesNotContain(injected, m =>
            m.Role == ChatRole.User &&
            (m.Text ?? "").Contains("handoff keyword", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoKeyword_NoAgentsHaveRun_FallsBackToDefault()
    {
        // At session start nothing has run — must still select the default agent.
        var routes = Array.Empty<KeywordSelectionStrategy.RouteEntry>();
        var strategy = new KeywordSelectionStrategy(routes, defaultAgentName: "Planner");

        var agents = new[] { MakeAgent("Planner"), MakeAgent("Developer") };
        var history = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "initial task")
        };

        var selected = await SelectAsync(strategy, agents, history);

        Assert.Equal("Planner", selected?.Name);
    }

    // Helper: delegate-based validator for parametric tests.
    private sealed class DelegateValidator(Func<IList<ChatMessage>, RoutingValidationResult> fn)
        : IRoutingValidator
    {
        public Task<RoutingValidationResult> ValidateAsync(
            IList<ChatMessage> history,
            CancellationToken cancellationToken = default)
            => Task.FromResult(fn(history));
    }

    // Minimal stub IChatClient — never makes network calls.
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
