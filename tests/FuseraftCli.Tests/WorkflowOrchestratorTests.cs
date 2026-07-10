using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Orchestration;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="WorkflowOrchestrator"/>'s topology/route-table construction — the
/// part that proves cycles work as plain, uniform routes (no forward/back distinction, no
/// BFS layer classification) rather than requiring <see cref="GraphOrchestrator"/>'s
/// phase-restart mechanism. No live agent execution — consistent with this repo's existing
/// orchestrator-testing convention (no orchestrator here is tested end-to-end with live or
/// scripted agents; <see cref="AgentFactory.Create"/> only builds an <c>AIAgent</c> wrapper,
/// it never makes a network call, so constructing one in a test is safe).
/// </summary>
public sealed class WorkflowOrchestratorTests : IDisposable
{
    // Distinct from AgentFactoryTests.FakeApiKeyVar — xUnit runs test classes in parallel by
    // default, and Environment.SetEnvironmentVariable is process-global state, so two classes
    // sharing one env var name race each other's constructor/Dispose.
    private const string FakeApiKeyVar = "FUSERAFT_WORKFLOW_TEST_API_KEY";
    private const string FakeApiKey    = "sk-test-key-not-used-in-unit-tests";

    private readonly PluginRegistry _registry;
    private readonly AgentFactory   _agentFactory;

    public WorkflowOrchestratorTests()
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ModelConfig FakeModel() => new()
    {
        ModelId      = "grok-4-1-fast-reasoning",
        Endpoint     = "https://api.x.ai/v1",
        ApiKeyEnvVar = FakeApiKeyVar
    };

    private WorkflowOrchestrator NewOrchestrator(OrchestrationConfig config) =>
        new(config, _agentFactory, NullLogger<WorkflowOrchestrator>.Instance);

    // Mirrors the shipped `graph` init template's Pipeline topology (InitTemplates.Graph.cs):
    // planner -> developer -> tester -> reviewer -> approved, with cycles back to developer
    // (from tester and reviewer) and back to planner (from developer).
    private static OrchestrationConfig PipelineConfig() => new()
    {
        Name = "pipeline-workflow-test",
        Agents =
        [
            new AgentConfig { Name = "Planner",   Instructions = "plan",   Model = FakeModel() },
            new AgentConfig { Name = "Developer",  Instructions = "code",  Model = FakeModel() },
            new AgentConfig { Name = "Tester",     Instructions = "test",  Model = FakeModel() },
            new AgentConfig { Name = "Reviewer",   Instructions = "review", Model = FakeModel() },
            new AgentConfig { Name = "Approved",   Instructions = "done",  Model = FakeModel() },
        ],
        Selection = new SelectionStrategyConfig
        {
            Type  = "workflow",
            Graph = new GraphConfig
            {
                EntryNode = "planner",
                Nodes =
                [
                    new GraphNodeConfig { Id = "planner",   Agent = "Planner" },
                    new GraphNodeConfig { Id = "developer", Agent = "Developer" },
                    new GraphNodeConfig { Id = "tester",    Agent = "Tester" },
                    new GraphNodeConfig { Id = "reviewer",  Agent = "Reviewer" },
                    new GraphNodeConfig { Id = "approved",  Agent = "Approved", Terminal = true },
                ],
                Edges =
                [
                    new GraphEdgeConfig { From = "planner",   To = "developer", Keyword = "HANDOFF TO DEVELOPER" },
                    new GraphEdgeConfig { From = "developer", To = "tester",    Keyword = "HANDOFF TO TESTER" },
                    new GraphEdgeConfig { From = "tester",    To = "reviewer",  Keyword = "HANDOFF TO REVIEWER" },
                    new GraphEdgeConfig { From = "reviewer",  To = "approved",  Keyword = "APPROVED" },
                    // Cycles — no forward/back distinction, just ordinary edges.
                    new GraphEdgeConfig { From = "tester",    To = "developer", Keyword = "BUGS FOUND" },
                    new GraphEdgeConfig { From = "reviewer",  To = "developer", Keyword = "REVISION REQUIRED" },
                    new GraphEdgeConfig { From = "developer", To = "planner",   Keyword = "REPLAN REQUIRED" },
                ]
            }
        }
    };

    private static Dictionary<string, GraphNodeConfig> NodeById(GraphConfig cfg) =>
        cfg.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

    // ── Every edge becomes a plain Route — cycles included ────────────────────

    [Fact]
    public void BuildNodeRouteTables_ForwardEdge_BecomesRoute()
    {
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph!;
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        var route = tables["planner"].Routes["HANDOFF TO DEVELOPER"];
        Assert.Equal("developer", route.NextExecutorId);
        Assert.Equal("Developer", route.NextExecutorName);
    }

    [Fact]
    public void BuildNodeRouteTables_CycleEdge_BecomesRoute_JustLikeForwardEdge()
    {
        // "BUGS FOUND" routes tester -> developer, even though developer is declared and
        // executes earlier in the pipeline. There is no BFS layer check, no PhaseBreakKeywords
        // bucket — it is wired identically to any other route.
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph!;
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        var route = tables["tester"].Routes["BUGS FOUND"];
        Assert.Equal("developer", route.NextExecutorId);
        Assert.Equal("Developer", route.NextExecutorName);

        // Confirm the route table type has no notion of "back" at all for this entry.
        Assert.Empty(tables["tester"].PhaseBreakKeywords);
    }

    [Fact]
    public void BuildNodeRouteTables_BothDirectionsOfACycle_CoexistAsOrdinaryRoutes()
    {
        // developer -> tester ("HANDOFF TO TESTER") and tester -> developer ("BUGS FOUND")
        // are both present simultaneously as plain Routes entries on their respective nodes.
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph!;
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        Assert.True(tables["developer"].Routes.ContainsKey("HANDOFF TO TESTER"));
        Assert.True(tables["tester"].Routes.ContainsKey("BUGS FOUND"));
    }

    [Fact]
    public void BuildNodeRouteTables_MultipleCyclesIntoSameTarget_AllRegistered()
    {
        // Both "BUGS FOUND" (from tester) and "REVISION REQUIRED" (from reviewer) cycle back
        // to developer — distinct keywords on distinct source nodes, no collision.
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph!;
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        Assert.Equal("developer", tables["tester"].Routes["BUGS FOUND"].NextExecutorId);
        Assert.Equal("developer", tables["reviewer"].Routes["REVISION REQUIRED"].NextExecutorId);
    }

    // ── Terminal node validators ────────────────────────────────────────────

    [Fact]
    public void BuildNodeRouteTables_TerminalNodeWithValidators_PopulatesTerminalValidators()
    {
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph! with
        {
            Nodes = config.Selection.Graph!.Nodes
                .Select(n => n.Id == "approved" ? n with { Validators = ["RequireShellPass"] } : n)
                .ToList()
        };
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        Assert.Single(tables["approved"].TerminalValidators);
    }

    // ── ReviewerType ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildNodeRouteTables_ReviewerTypeNode_PopulatesIsReviewerType()
    {
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph! with
        {
            Nodes = config.Selection.Graph!.Nodes
                .Select(n => n.Id == "reviewer" ? n with { ReviewerType = true } : n)
                .ToList()
        };
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        Assert.True(tables["reviewer"].IsReviewerType);
        Assert.False(tables["tester"].IsReviewerType);
    }

    // ── SourceAgents restriction ─────────────────────────────────────────────

    [Fact]
    public void BuildNodeRouteTables_EdgeWithSourceAgentsNotMatchingNode_IsSkipped()
    {
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph! with
        {
            Edges = config.Selection.Graph!.Edges
                .Select(e => e.Keyword == "BUGS FOUND" ? e with { SourceAgents = ["SomeOtherAgent"] } : e)
                .ToList()
        };
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        Assert.False(tables.TryGetValue("tester", out var table) && table.Routes.ContainsKey("BUGS FOUND"));
    }

    // ── ForeignSendForwardKeywords ────────────────────────────────────────────

    [Fact]
    public void BuildNodeRouteTables_ForeignKeywords_ExcludeNodesOwnKeywords()
    {
        var config = PipelineConfig();
        var graphCfg = config.Selection.Graph!;
        var tables = NewOrchestrator(config).BuildNodeRouteTables(graphCfg, NodeById(graphCfg));

        // "tester" owns "BUGS FOUND" — it must not appear in its own ForeignSendForwardKeywords.
        Assert.DoesNotContain("BUGS FOUND", tables["tester"].ForeignSendForwardKeywords);
        // "tester" does not own "APPROVED" — it should be listed as a foreign keyword.
        Assert.Contains("APPROVED", tables["tester"].ForeignSendForwardKeywords);
    }

    // ── Public-surface smoke test ────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_ThrowsInvalidOperationException_WhenSelectionGraphIsNull()
    {
        var config = PipelineConfig() with
        {
            Selection = new SelectionStrategyConfig { Type = "workflow", Graph = null }
        };
        var orchestrator = NewOrchestrator(config);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in orchestrator.StreamAsync("task")) { }
        });
    }
}
