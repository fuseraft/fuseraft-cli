using System.Threading.Channels;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Workflow;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for the parallel fan-out/fan-in additions:
/// <see cref="AgentRouteTable.ParallelKeywords"/>,
/// <see cref="KeywordDetector"/>, <see cref="CorrectionEngine"/>,
/// <see cref="GraphOrchestrator.ForkContext"/>, and
/// <see cref="GraphOrchestrator.MergeParallelContexts"/>.
/// </summary>
public sealed class GraphOrchestratorParallelTests
{
    // -----------------------------------------------------------------------
    // AgentRouteTable.ParallelKeywords — set semantics
    // -----------------------------------------------------------------------

    [Fact]
    public void ParallelKeywords_IsEmptyByDefault()
    {
        var table = new AgentRouteTable();
        Assert.Empty(table.ParallelKeywords);
    }

    [Fact]
    public void ParallelKeywords_IsCaseInsensitive()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        Assert.Contains("begin parallel analysis", table.ParallelKeywords);
        Assert.Contains("BEGIN PARALLEL ANALYSIS", table.ParallelKeywords);
        Assert.Contains("Begin Parallel Analysis",  table.ParallelKeywords);
    }

    [Fact]
    public void ParallelKeywords_DuplicateAdd_CountRemainsOne()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("PARALLEL DISPATCH");
        table.ParallelKeywords.Add("PARALLEL DISPATCH");

        Assert.Single(table.ParallelKeywords);
    }

    // -----------------------------------------------------------------------
    // GraphConfig.Parallel flag
    // -----------------------------------------------------------------------

    [Fact]
    public void GraphNodeConfig_Parallel_DefaultsFalse()
    {
        var node = new GraphNodeConfig { Id = "worker", Agent = "WorkerAgent" };
        Assert.False(node.Parallel);
    }

    [Fact]
    public void GraphNodeConfig_Parallel_CanBeSetTrue()
    {
        var node = new GraphNodeConfig { Id = "worker", Agent = "WorkerAgent", Parallel = true };
        Assert.True(node.Parallel);
    }

    [Fact]
    public void GraphNodeConfig_Parallel_And_Terminal_AreIndependentFlags()
    {
        var node = new GraphNodeConfig { Id = "x", Agent = "A", Parallel = true, Terminal = true };
        Assert.True(node.Parallel);
        Assert.True(node.Terminal);
    }

    // -----------------------------------------------------------------------
    // KeywordDetector.DetectKeywords — ParallelKeywords included
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectKeywords_ParallelKeyword_OnOwnLine_IsDetected()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.DetectKeywords(
            "Work is ready.\n\nBEGIN PARALLEL ANALYSIS\n\nSome trailing text.", table);

        Assert.Single(result);
        Assert.Equal("BEGIN PARALLEL ANALYSIS", result[0]);
    }

    [Fact]
    public void DetectKeywords_ParallelKeyword_EmbeddedInProse_NotDetected()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.DetectKeywords(
            "We should BEGIN PARALLEL ANALYSIS of this data.", table);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectKeywords_ParallelKeyword_AlreadyInRoutes_NotDuplicated()
    {
        // If a keyword appears in both Routes and ParallelKeywords (config oddity),
        // it should only appear once in the result — Routes wins.
        var table = new AgentRouteTable();
        table.Routes["DISPATCH"] = new RouteInfo("node-a", "NodeA", []);
        table.ParallelKeywords.Add("DISPATCH");

        var result = KeywordDetector.DetectKeywords("DISPATCH", table);

        Assert.Single(result);
        Assert.Equal("DISPATCH", result[0]);
    }

    [Fact]
    public void DetectKeywords_ParallelAndForward_BothOnOwnLines_CountsAsMultiple()
    {
        var table = new AgentRouteTable();
        table.Routes["HANDOFF TO TESTER"] = new RouteInfo("tester", "Tester", []);
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.DetectKeywords(
            "HANDOFF TO TESTER\n\nBEGIN PARALLEL ANALYSIS", table);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DetectKeywords_ParallelKeyword_CaseInsensitiveMatch()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.DetectKeywords("begin parallel analysis", table);

        Assert.Single(result);
    }

    [Fact]
    public void DetectKeywords_ParallelKeyword_MarkdownBold_Stripped_StillDetected()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.DetectKeywords("**BEGIN PARALLEL ANALYSIS**", table);

        Assert.Single(result);
    }

    // -----------------------------------------------------------------------
    // KeywordDetector.ExtractHandoffToolCallKeyword — ParallelKeywords included
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractHandoffToolCallKeyword_ParallelKeyword_IsRecognized()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var msg = HandoffMessage("BEGIN PARALLEL ANALYSIS");

        var result = KeywordDetector.ExtractHandoffToolCallKeyword([msg], table);

        Assert.Equal("BEGIN PARALLEL ANALYSIS", result);
    }

    [Fact]
    public void ExtractHandoffToolCallKeyword_KeywordNotInAnySet_ReturnsNull()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var msg = HandoffMessage("SOME UNKNOWN KEYWORD");

        var result = KeywordDetector.ExtractHandoffToolCallKeyword([msg], table);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractHandoffToolCallKeyword_ParallelKeyword_CaseInsensitive()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var msg = HandoffMessage("begin parallel analysis");

        var result = KeywordDetector.ExtractHandoffToolCallKeyword([msg], table);

        Assert.NotNull(result);
    }

    // -----------------------------------------------------------------------
    // CorrectionEngine.BuildValidKeywordList — ParallelKeywords included
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildValidKeywordList_ParallelKeyword_AppearsInOutput()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var list = CorrectionEngine.BuildValidKeywordList(table);

        Assert.Contains("'BEGIN PARALLEL ANALYSIS'", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildValidKeywordList_OnlyParallelKeywords_ReturnsKeywordsNotPlaceholder()
    {
        var table = new AgentRouteTable();
        table.ParallelKeywords.Add("PARALLEL DISPATCH");

        var list = CorrectionEngine.BuildValidKeywordList(table);

        Assert.DoesNotContain("(none configured", list);
        Assert.Contains("'PARALLEL DISPATCH'", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildValidKeywordList_EmptyTable_ReturnsPlaceholder()
    {
        var table  = new AgentRouteTable();
        var list   = CorrectionEngine.BuildValidKeywordList(table);
        Assert.Contains("(none configured", list);
    }

    [Fact]
    public void BuildValidKeywordList_DuplicateAcrossRoutesAndParallel_DeduplicatedToOne()
    {
        var table = new AgentRouteTable();
        table.Routes["HANDOFF TO TESTER"] = new RouteInfo("tester", "Tester", []);
        table.ParallelKeywords.Add("HANDOFF TO TESTER"); // same keyword in both

        var list = CorrectionEngine.BuildValidKeywordList(table);

        // Keyword should appear exactly once.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            list, "HANDOFF TO TESTER", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Single(matches);
    }

    [Fact]
    public void BuildValidKeywordList_AllThreeSets_AllPresent()
    {
        var table = new AgentRouteTable();
        table.Routes["HANDOFF TO TESTER"]   = new RouteInfo("tester", "Tester", []);
        table.PhaseBreakKeywords.Add("BUGS FOUND");
        table.ParallelKeywords.Add("BEGIN PARALLEL ANALYSIS");

        var list = CorrectionEngine.BuildValidKeywordList(table);

        Assert.Contains("'HANDOFF TO TESTER'",        list, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'BUGS FOUND'",               list, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'BEGIN PARALLEL ANALYSIS'",  list, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // GraphOrchestrator.ForkContext — isolation and shared sink
    // -----------------------------------------------------------------------

    [Fact]
    public void ForkContext_CopiesHistory_Completely()
    {
        var (_, parent) = MakeContext(turnIndex: 0);
        parent.History.Add(User("task"));
        parent.History.Add(Asst("response"));

        var fork = GraphOrchestrator.ForkContext(parent);

        Assert.Equal(2, fork.History.Count);
        Assert.Equal("task",     TextOf(fork.History[0]));
        Assert.Equal("response", TextOf(fork.History[1]));
    }

    [Fact]
    public void ForkContext_ForkAdd_DoesNotAffectParent()
    {
        var (_, parent) = MakeContext();
        parent.History.Add(User("task"));

        var fork = GraphOrchestrator.ForkContext(parent);
        fork.History.Add(Asst("fork-only"));

        Assert.Single(parent.History);
        Assert.Equal(2, fork.History.Count);
    }

    [Fact]
    public void ForkContext_ParentAdd_DoesNotAffectFork()
    {
        var (_, parent) = MakeContext();
        parent.History.Add(User("task"));

        var fork = GraphOrchestrator.ForkContext(parent);
        parent.History.Add(User("added after fork"));

        Assert.Single(fork.History); // fork is unaffected
    }

    [Fact]
    public void ForkContext_SharesMessageSink()
    {
        var (sink, parent) = MakeContext();

        var fork = GraphOrchestrator.ForkContext(parent);

        Assert.Same(sink, fork.MessageSink);
    }

    [Fact]
    public void ForkContext_CopiesTurnIndexAndCumulativeTokens()
    {
        var (_, parent) = MakeContext(turnIndex: 7, tokens: 1500);

        var fork = GraphOrchestrator.ForkContext(parent);

        Assert.Equal(7,    fork.TurnIndex);
        Assert.Equal(1500, fork.CumulativeTokens);
    }

    [Fact]
    public void ForkContext_EmptyHistory_ProducesEmptyFork()
    {
        var (_, parent) = MakeContext();

        var fork = GraphOrchestrator.ForkContext(parent);

        Assert.Empty(fork.History);
    }

    // Regression coverage: concurrent parallel branches used to seed every fork's TurnIndex
    // at the same value (parent.TurnIndex), so two branches completing after the same number
    // of turns emitted colliding TurnIndex values into the shared MessageSink/event log.
    // ForkContext now offsets each branch by branchIndex * a large stride so their ranges
    // never overlap; MergeParallelContexts recovers the actual turn count taken and
    // reconciles the parent back to a normal (non-inflated) continuation point.

    [Fact]
    public void ForkContext_DifferentBranchIndices_ProduceNonCollidingTurnIndexRanges()
    {
        var (_, parent) = MakeContext(turnIndex: 5);

        var branch0 = GraphOrchestrator.ForkContext(parent, branchIndex: 0);
        var branch1 = GraphOrchestrator.ForkContext(parent, branchIndex: 1);
        var branch2 = GraphOrchestrator.ForkContext(parent, branchIndex: 2);

        // Even before any turns are taken, each branch starts in a disjoint range.
        Assert.NotEqual(branch0.TurnIndex, branch1.TurnIndex);
        Assert.NotEqual(branch1.TurnIndex, branch2.TurnIndex);
        Assert.NotEqual(branch0.TurnIndex, branch2.TurnIndex);
    }

    [Fact]
    public void MergeParallelContexts_SameTurnCountAcrossBranches_NoLongerCollides_AndParentAdvancesNormally()
    {
        var (_, parent) = MakeContext(turnIndex: 5);

        // Simulate two branches that each independently take exactly 2 turns — the exact
        // scenario that used to produce identical TurnIndex values in both branches.
        var branchA = GraphOrchestrator.ForkContext(parent, branchIndex: 0);
        var turnA1  = branchA.TurnIndex++;
        var turnA2  = branchA.TurnIndex++;

        var branchB = GraphOrchestrator.ForkContext(parent, branchIndex: 1);
        var turnB1  = branchB.TurnIndex++;
        var turnB2  = branchB.TurnIndex++;

        // The bug: without branch offsets, turnA1==turnB1 and turnA2==turnB2.
        Assert.NotEqual(turnA1, turnB1);
        Assert.NotEqual(turnA2, turnB2);

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint: 0,
            [("a", "A", branchA, 0), ("b", "B", branchB, 1)]);

        // Both branches took exactly 2 turns — the parent should advance by 2 from its
        // pre-fork value (5), not by some inflated branch-offset-laden number.
        Assert.Equal(7, parent.TurnIndex);
    }

    // -----------------------------------------------------------------------
    // GraphOrchestrator.MergeParallelContexts — history merging
    // -----------------------------------------------------------------------

    [Fact]
    public void MergeParallelContexts_InjectsHeaderAndPostForkMessages()
    {
        var (sink, parent) = MakeContext(tokens: 100, turnIndex: 1);
        parent.History.Add(User("task"));
        int forkPoint = parent.History.Count;

        var child = MakeForkedChild(parent, forkPoint);
        child.History.Add(Asst("worker output"));

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("worker_a", "WorkerA", child, 0)]);

        // parent: original task + header + worker output = 3
        Assert.Equal(3, parent.History.Count);
        Assert.Contains("WorkerA", TextOf(parent.History[1]), StringComparison.Ordinal);
        Assert.Equal("worker output", TextOf(parent.History[2]));
    }

    [Fact]
    public void MergeParallelContexts_TwoChildren_BothOutputsMergedInOrder()
    {
        var (sink, parent) = MakeContext();
        int forkPoint = 0;

        var child_a = MakeForkedChild(parent, forkPoint);
        child_a.History.Add(Asst("output from A"));

        var child_b = MakeForkedChild(parent, forkPoint);
        child_b.History.Add(Asst("output from B"));

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("n_a", "AgentA", child_a, 0), ("n_b", "AgentB", child_b, 0)]);

        // header_a + output_a + header_b + output_b = 4
        Assert.Equal(4, parent.History.Count);
        Assert.Contains("AgentA",      TextOf(parent.History[0]), StringComparison.Ordinal);
        Assert.Equal("output from A",  TextOf(parent.History[1]));
        Assert.Contains("AgentB",      TextOf(parent.History[2]), StringComparison.Ordinal);
        Assert.Equal("output from B",  TextOf(parent.History[3]));
    }

    [Fact]
    public void MergeParallelContexts_OnlyPostForkMessages_Included()
    {
        var (_, parent) = MakeContext();
        parent.History.Add(User("pre-fork message"));
        int forkPoint = parent.History.Count; // = 1

        var child = MakeForkedChild(parent, forkPoint);
        // child.History[0] is the pre-fork copy; add a post-fork message at index 1
        child.History.Add(Asst("post-fork output"));

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("n", "Agent", child, 0)]);

        // parent: pre-fork (1) + header (1) + post-fork output (1) = 3
        Assert.Equal(3, parent.History.Count);
        Assert.Equal("post-fork output", TextOf(parent.History[2]));
    }

    [Fact]
    public void MergeParallelContexts_TurnIndex_TakesMaxAcrossChildren()
    {
        var (sink, parent) = MakeContext(turnIndex: 1);
        int forkPoint = 0;

        var child_a = MakeForkedChild(parent, forkPoint);
        child_a.TurnIndex = 4;

        var child_b = MakeForkedChild(parent, forkPoint);
        child_b.TurnIndex = 6;

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("a", "A", child_a, 0), ("b", "B", child_b, 0)]);

        Assert.Equal(6, parent.TurnIndex);
    }

    [Fact]
    public void MergeParallelContexts_TurnIndex_ParentWins_WhenHigherThanChildren()
    {
        var (_, parent) = MakeContext(turnIndex: 10);
        int forkPoint = 0;

        var child = MakeForkedChild(parent, forkPoint);
        child.TurnIndex = 3; // lower than parent

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint, [("n", "A", child, 0)]);

        Assert.Equal(10, parent.TurnIndex);
    }

    [Fact]
    public void MergeParallelContexts_TokenCounts_Aggregated()
    {
        var (_, parent) = MakeContext(tokens: 500);
        int forkPoint = 0;

        // Each child spent tokens beyond the fork baseline of 500.
        var child_a = MakeForkedChild(parent, forkPoint);
        child_a.CumulativeTokens = 800; // delta = 300

        var child_b = MakeForkedChild(parent, forkPoint);
        child_b.CumulativeTokens = 650; // delta = 150

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("a", "A", child_a, 0), ("b", "B", child_b, 0)]);

        // 500 + 300 + 150 = 950
        Assert.Equal(950, parent.CumulativeTokens);
    }

    [Fact]
    public void MergeParallelContexts_NegativeTokenDelta_Clamped_ParentNotDecremented()
    {
        // A pathological fork where child ends up with fewer tokens than parent (shouldn't
        // happen in practice, but the clamp to 0 must prevent decrementing the parent).
        var (_, parent) = MakeContext(tokens: 500);
        int forkPoint = 0;

        var child = MakeForkedChild(parent, forkPoint);
        child.CumulativeTokens = 100; // impossible delta = -400

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint, [("n", "A", child, 0)]);

        // Math.Max(0, -400) = 0 → parent stays at 500
        Assert.Equal(500, parent.CumulativeTokens);
    }

    [Fact]
    public void MergeParallelContexts_EmptyChildHistory_OnlyHeaderInjected()
    {
        var (_, parent) = MakeContext();
        int forkPoint = 0;

        // child has no messages at all (not even a pre-fork copy)
        var (_, child) = MakeContext();

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint, [("n", "AgentX", child, 0)]);

        // Only the header should be injected; no content messages.
        Assert.Single(parent.History);
        Assert.Contains("AgentX", TextOf(parent.History[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void MergeParallelContexts_HeaderContainsNodeId()
    {
        var (_, parent) = MakeContext();
        int forkPoint = 0;

        var (_, child) = MakeContext();

        GraphOrchestrator.MergeParallelContexts(parent, forkPoint,
            [("analyzer_a", "AnalyzerAgent", child, 0)]);

        var header = TextOf(parent.History[0]);
        Assert.Contains("analyzer_a", header, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (ChannelWriter<AgentMessage> Sink, AgentContext Ctx) MakeContext(
        int turnIndex = 0, int tokens = 0)
    {
        var sink = Channel.CreateUnbounded<AgentMessage>().Writer;
        var ctx  = new AgentContext
        {
            MessageSink      = sink,
            TurnIndex        = turnIndex,
            CumulativeTokens = tokens,
        };
        return (sink, ctx);
    }

    /// <summary>
    /// Creates a child context that mirrors the parent's pre-fork state, replicating
    /// exactly what <see cref="GraphOrchestrator.ForkContext"/> does.
    /// </summary>
    private static AgentContext MakeForkedChild(AgentContext parent, int forkPoint)
    {
        var child = new AgentContext
        {
            MessageSink      = parent.MessageSink,
            TurnIndex        = parent.TurnIndex,
            CumulativeTokens = parent.CumulativeTokens,
            CurrentState     = parent.CurrentState,
        };
        // Copy only the pre-fork portion of history.
        for (int i = 0; i < forkPoint; i++)
            child.History.Add(parent.History[i]);
        return child;
    }

    private static ChatMessage User(string text) => new(ChatRole.User,      text);
    private static ChatMessage Asst(string text) => new(ChatRole.Assistant, text);

    private static string TextOf(ChatMessage msg) =>
        string.Concat(msg.Contents.OfType<TextContent>().Select(t => t.Text));

    private static ChatMessage HandoffMessage(string keyword)
    {
        var msg = new ChatMessage(ChatRole.Assistant, (string?)null);
        msg.Contents =
        [
            new FunctionCallContent(
                "call-1",
                HandoffPlugin.FunctionName,
                new Dictionary<string, object?> { [HandoffPlugin.ArgumentName] = keyword }),
        ];
        return msg;
    }
}
