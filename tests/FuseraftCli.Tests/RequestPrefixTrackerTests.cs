using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

public class RequestPrefixTrackerTests
{
    private static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.System, "You are a test."),
        new(ChatRole.User, "read it"),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file",
            new Dictionary<string, object?> { ["path"] = JsonSerializer.SerializeToElement("a.cs") })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "class A {}")]),
    ];

    [Fact]
    public void FirstRequest_HasNothingToCompareWith()
    {
        Assert.Equal((null, 0), new RequestPrefixTracker().Observe(Conversation()));
    }

    [Fact]
    public void AppendedRequest_KeepsThePrefix()
    {
        var tracker = new RequestPrefixTracker();
        var messages = Conversation();
        tracker.Observe(messages);

        messages.Add(new ChatMessage(ChatRole.Assistant, "done"));

        Assert.Equal((null, 4), tracker.Observe(messages));
    }

    [Fact]
    public void RewrittenToolResult_ReportsItsIndex()
    {
        var tracker = new RequestPrefixTracker();
        tracker.Observe(Conversation());

        var rewritten = Conversation();
        rewritten[3] = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "[ELIDED]")]);
        rewritten.Add(new ChatMessage(ChatRole.Assistant, "done"));

        Assert.Equal((3, 4), tracker.Observe(rewritten));
    }

    [Fact]
    public void ChangedCallArguments_ReportTheirIndex()
    {
        var tracker = new RequestPrefixTracker();
        tracker.Observe(Conversation());

        var rewritten = Conversation();
        rewritten[2] = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file",
            new Dictionary<string, object?> { ["path"] = JsonSerializer.SerializeToElement("b.cs") })]);

        Assert.Equal(2, tracker.Observe(rewritten).ChangedAt);
    }

    [Fact]
    public void ShorterUnchangedRequest_KeepsThePrefix()
    {
        var tracker = new RequestPrefixTracker();
        tracker.Observe(Conversation());

        Assert.Equal((null, 4), tracker.Observe(Conversation()[..2]));
    }
}
