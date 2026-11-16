using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Context;

namespace FuseraftCli.Tests;

public sealed class ContextAssemblerTests
{
    private static ChatMessage Writer(params AIContent[] contents) => new(ChatRole.Assistant, contents) { AuthorName = "Writer" };

    // A turn as the orchestrator records it: tool calls and results, a handoff, then the final text,
    // followed by what the state machine injects when the handoff is refused.
    private static List<ChatMessage> RefusedTurn() =>
    [
        new(ChatRole.User, "Write hello.txt."),
        Writer(new FunctionCallContent("c1", "write_file", new Dictionary<string, object?> { ["path"] = "hello.txt" })),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
        Writer(new FunctionCallContent("c2", HandoffPlugin.FunctionName, new Dictionary<string, object?> { [HandoffPlugin.ArgumentName] = "end" })),
        new(ChatRole.Tool, [new FunctionResultContent("c2", "ok")]),
        Writer(new TextContent("File written. Handing off.")),
        new(ChatRole.User, "The operator didn't approve the handoff to end.\n1. Keep working on this step."),
        new(ChatRole.User, "[fuseraft:blocked Writer→end]"),
    ];

    [Fact]
    public async Task RefusedHandoff_ReachesTheAgentThatSeesOnlyItsHandoff()
    {
        var result = await new ContextAssembler().AssembleForAgentAsync(
            "Writer", "Write hello.txt.", [new ContextSource { Source = "own_history:4" }], RefusedTurn());

        Assert.Contains(result.Messages, m => m.Role == ChatRole.User && m.Text.StartsWith("The operator didn't approve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorrectionForAnotherAgent_IsNotShown()
    {
        var history = RefusedTurn();
        history.Add(new ChatMessage(ChatRole.Assistant, "Reviewing.") { AuthorName = "Reviewer" });
        history.Add(new ChatMessage(ChatRole.User, "MISSING ARTIFACT — Handoff 'Reviewer' → 'end' is blocked"));

        var result = await new ContextAssembler().AssembleForAgentAsync(
            "Writer", "Write hello.txt.", [new ContextSource { Source = "own_history:4" }], history);

        Assert.DoesNotContain(result.Messages, m => m.Text.StartsWith("MISSING ARTIFACT", StringComparison.Ordinal));
        Assert.Contains(result.Messages, m => m.Text.StartsWith("The operator didn't approve", StringComparison.Ordinal));
    }
}
