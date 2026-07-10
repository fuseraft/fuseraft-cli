using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Strategies;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression coverage for <see cref="StructuredSelectionStrategy"/>'s failure handling —
/// it used to bypass the shared classify → <see cref="FailureHandlingConfig"/> → escalate
/// pipeline entirely (a hardcoded retry count with no way to configure policy). These tests
/// prove the strategy now actually reads and honors an injected <see cref="FailureHandlingConfig"/>,
/// rather than merely still working under the (coincidentally identical) default threshold.
/// </summary>
public sealed class StructuredSelectionStrategyTests : IDisposable
{
    private const string FakeApiKeyVar = "FUSERAFT_STRUCTURED_TEST_API_KEY";
    private const string FakeApiKey    = "sk-test-key-not-used-in-unit-tests";

    private readonly PluginRegistry _registry;
    private readonly AgentFactory   _agentFactory;

    public StructuredSelectionStrategyTests()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, FakeApiKey);
        _registry     = new PluginRegistry(NullLoggerFactory.Instance).RegisterDefaults();
        _agentFactory = new AgentFactory(new ChatClientFactory(), _registry);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, null);
        _registry.Dispose();
    }

    private AIAgent BuildAgent(string name) => _agentFactory.Create(new AgentConfig
    {
        Name  = name,
        Model = new ModelConfig { ModelId = "grok-4-1-fast-reasoning", Endpoint = "https://api.x.ai/v1", ApiKeyEnvVar = FakeApiKeyVar }
    });

    private static List<ChatMessage> NonJsonHistoryFrom(string agentName) =>
    [
        new(ChatRole.User, "start"),
        new(ChatRole.Assistant, "This is not JSON at all.") { AuthorName = agentName },
    ];

    private StructuredSelectionStrategy.RouteEntry Route(string agent) =>
        new(AgentName: agent, Condition: new StructuredCondition { Field = "status", Is = "done" }, SourceAgents: null);

    [Fact]
    public async Task CustomThreshold_EscalatesExactlyAtConfiguredCount_NotHardcodedThree()
    {
        var agent = BuildAgent("Worker");
        var strategy = new StructuredSelectionStrategy(
            [Route("Worker")],
            defaultAgentName: "Worker",
            logger: null,
            failureHandling: new FailureHandlingConfig
            {
                // JSON-parse failures classify as InvalidTransition (no marker in the error
                // text matches MissingEvidence/ConflictingEvidence phrases).
                InvalidTransition = new FailureTypeConfig { Action = FailureAction.Reinstruct, Threshold = 1 },
            });

        var history = NonJsonHistoryFrom("Worker");

        // With Threshold = 1, the very first parse failure must escalate — proving the
        // strategy reads _failureHandling rather than a hardcoded retry count.
        await Assert.ThrowsAsync<ValidatorStuckException>(
            () => strategy.SelectAsync([agent], history));
    }

    [Fact]
    public async Task EscalateToHumanAction_ThrowsImmediatelyOnFirstFailure()
    {
        var agent = BuildAgent("Worker");
        var strategy = new StructuredSelectionStrategy(
            [Route("Worker")],
            defaultAgentName: "Worker",
            logger: null,
            failureHandling: new FailureHandlingConfig
            {
                InvalidTransition = new FailureTypeConfig { Action = FailureAction.EscalateToHuman, Threshold = 10 },
            });

        var history = NonJsonHistoryFrom("Worker");

        // EscalateToHuman must bypass the threshold entirely, even though it's set to 10.
        await Assert.ThrowsAsync<ValidatorStuckException>(
            () => strategy.SelectAsync([agent], history));
    }

    [Fact]
    public async Task DefaultConfig_DoesNotEscalateBeforeThreshold()
    {
        var agent = BuildAgent("Worker");
        var strategy = new StructuredSelectionStrategy([Route("Worker")], defaultAgentName: "Worker");
        strategy.SetHistory(NonJsonHistoryFrom("Worker"));

        // Default InvalidTransition.Threshold is 3 — the first failure must not throw.
        var next = await strategy.SelectAsync([agent], NonJsonHistoryFrom("Worker"));

        Assert.Equal("Worker", next?.Name);
    }
}
