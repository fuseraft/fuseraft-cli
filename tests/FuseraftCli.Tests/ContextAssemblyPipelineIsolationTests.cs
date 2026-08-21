using Microsoft.Extensions.AI;
using fuseraft.Core.Models.Agents;
using fuseraft.Orchestration.Context;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for the Isolation-aware branch of <see cref="ContextAssemblyPipeline.AssembleAsync"/> —
/// the core behavioral flip of the agent-isolation protocol overhaul: <see cref="AgentIsolation.Fresh"/>
/// (the default) must never surface <c>SharedHistory</c> content, while <see cref="AgentIsolation.Shared"/>
/// preserves the pre-overhaul windowed-transcript fallback and <see cref="AgentIsolation.Fork"/> layers
/// a synthesized directive on top of the full transcript.
/// </summary>
public sealed class ContextAssemblyPipelineIsolationTests
{
    private static List<ChatMessage> SharedHistoryWithSecret() =>
    [
        new ChatMessage(ChatRole.User, "Investigate the outage.") { AuthorName = "Investigator" },
        new ChatMessage(ChatRole.Assistant, "SECRET_REASONING: tried X, ruled it out, tried Y.")
            { AuthorName = "Investigator" },
    ];

    [Fact]
    public async Task Fresh_agent_never_sees_shared_history_content()
    {
        var pipeline = new ContextAssemblyPipeline();
        var request = new AgentExecutionRequest
        {
            AgentName     = "Fixer",
            Task          = "Fix the outage.",
            SharedHistory = SharedHistoryWithSecret(),
            AgentConfig   = new AgentConfig { Name = "Fixer", Isolation = AgentIsolation.Fresh },
        };

        var assembled = await pipeline.AssembleAsync(request);

        Assert.DoesNotContain(assembled.Messages, m =>
            m.Text?.Contains("SECRET_REASONING", StringComparison.Ordinal) == true);
        Assert.Equal(
            fuseraft.Core.Models.Context.ContextAssemblyMetrics.Strategies.ArtifactSpec,
            assembled.Metrics.ContextStrategy);
    }

    [Fact]
    public async Task Fresh_agent_uses_directive_as_task_message_when_supplied()
    {
        var pipeline = new ContextAssemblyPipeline();
        var directive = new AgentDirective
        {
            Goal        = "Patch the null check in Parser.cs.",
            Background  = "Root cause confirmed: line 42 dereferences before the null guard.",
            Constraints = ["Do not change the public API."],
        };
        var request = new AgentExecutionRequest
        {
            AgentName     = "Fixer",
            Task          = "(original session task — should not appear verbatim)",
            SharedHistory = SharedHistoryWithSecret(),
            Directive     = directive,
            AgentConfig   = new AgentConfig { Name = "Fixer", Isolation = AgentIsolation.Fresh },
        };

        var assembled = await pipeline.AssembleAsync(request);

        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("Patch the null check in Parser.cs.", StringComparison.Ordinal) == true);
        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("Do not change the public API.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Fresh_agent_recovers_directive_from_last_handoff_call_when_not_supplied_directly()
    {
        var pipeline = new ContextAssemblyPipeline();
        var history = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "Investigate the outage.") { AuthorName = "Investigator" },
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "handoff", new Dictionary<string, object?>
                {
                    ["route_keyword"] = "HANDOFF TO FIXER",
                    ["goal"]          = "Patch the parser null check.",
                    ["background"]    = "Root cause already confirmed.",
                }),
            ])
            { AuthorName = "Investigator" },
        };
        var request = new AgentExecutionRequest
        {
            AgentName     = "Fixer",
            Task          = "(original session task)",
            SharedHistory = history,
            AgentConfig   = new AgentConfig { Name = "Fixer", Isolation = AgentIsolation.Fresh },
        };

        var assembled = await pipeline.AssembleAsync(request);

        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("Patch the parser null check.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Shared_agent_with_no_context_block_keeps_legacy_history_fallback()
    {
        var pipeline = new ContextAssemblyPipeline();
        var request = new AgentExecutionRequest
        {
            AgentName     = "Investigator",
            Task          = "Investigate the outage.",
            SharedHistory = SharedHistoryWithSecret(),
            AgentConfig   = new AgentConfig { Name = "Investigator", Isolation = AgentIsolation.Shared },
        };

        var assembled = await pipeline.AssembleAsync(request);

        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("SECRET_REASONING", StringComparison.Ordinal) == true);
        Assert.Equal(
            fuseraft.Core.Models.Context.ContextAssemblyMetrics.Strategies.SharedHistoryFallback,
            assembled.Metrics.ContextStrategy);
    }

    [Fact]
    public async Task Fork_agent_sees_full_history_plus_directive()
    {
        var pipeline = new ContextAssemblyPipeline();
        var directive = new AgentDirective { Goal = "Audit the session for inconsistencies." };
        var request = new AgentExecutionRequest
        {
            AgentName     = "Verifier",
            Task          = "Investigate the outage.",
            SharedHistory = SharedHistoryWithSecret(),
            Directive     = directive,
            AgentConfig   = new AgentConfig { Name = "Verifier", Isolation = AgentIsolation.Fork },
        };

        var assembled = await pipeline.AssembleAsync(request);

        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("SECRET_REASONING", StringComparison.Ordinal) == true);
        Assert.Contains(assembled.Messages, m =>
            m.Text?.Contains("Audit the session for inconsistencies.", StringComparison.Ordinal) == true);
    }
}
