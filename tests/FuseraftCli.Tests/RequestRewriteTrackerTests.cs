using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

public class RequestRewriteTrackerTests
{
    private static List<ChatMessage> Conversation(string result = "class A {}") =>
    [
        new(ChatRole.System, "You are a test."),
        new(ChatRole.User, "read it"),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file")]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", result)]),
    ];

    private static readonly AITool[] Tools = [AIFunctionFactory.Create(() => "x", "read_file", "Read a file.")];

    // One request: stored as given, with `dropped` after the superseded-content step and `sent` as sent.
    private static RequestChange Send(
        RequestRewriteTracker tracker, List<ChatMessage> stored, List<ChatMessage>? dropped = null,
        List<ChatMessage>? sent = null, IList<AITool>? tools = null, int attempt = 0, string? run = null)
    {
        tracker.ObserveInputs(run, stored, dropped ?? stored);
        return tracker.ObserveSent(run, sent ?? dropped ?? stored, tools ?? Tools, attempt);
    }

    [Fact]
    public void UnchangedPrefix_HasNoCause()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        var next = Conversation();
        next.Add(new ChatMessage(ChatRole.Assistant, "done"));

        Assert.Equal(new RequestChange(null, 4, null, false), Send(tracker, next));
    }

    [Fact]
    public void RewrittenStoredHistory_IsHistory()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        var change = Send(tracker, Conversation("[summary of earlier work]"));

        Assert.Equal((3, PrefixChangeCause.History), (change.ChangedAt, change.Cause));
    }

    [Fact]
    public void DroppedSupersededContent_IsStaleDrop()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        Assert.Equal(PrefixChangeCause.StaleDrop,
            Send(tracker, Conversation(), dropped: Conversation("[superseded]")).Cause);
    }

    [Fact]
    public void TrimmedOnlyAsSent_IsInTurnTrim()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        Assert.Equal(PrefixChangeCause.InTurnTrim,
            Send(tracker, Conversation(), sent: Conversation("[elided]")).Cause);
    }

    [Fact]
    public void RetryAfterOverflow_IsOverflowTrim()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        Assert.Equal(PrefixChangeCause.OverflowTrim,
            Send(tracker, Conversation(), sent: Conversation("[cut]"), attempt: 1).Cause);
    }

    [Fact]
    public void ChangedTools_IsTools()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());

        var change = Send(tracker, Conversation(), tools: [AIFunctionFactory.Create(() => "x", "write_file", "Write a file.")]);

        Assert.Equal((PrefixChangeCause.Tools, true), (change.Cause, change.ToolsChanged));
    }

    [Fact]
    public void SubagentRuns_AreTrackedApart()
    {
        var tracker = new RequestRewriteTracker();
        Send(tracker, Conversation());
        Send(tracker, [new(ChatRole.System, "explorer"), new(ChatRole.User, "find it")], run: "run-1");

        var next = Conversation();
        next.Add(new ChatMessage(ChatRole.Assistant, "done"));

        Assert.Null(Send(tracker, next).Cause);
    }
}
