using fuseraft.Cli.Tui;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>Sends a subagent's tool calls to the VS Code panel, tagged with the subagent's name, as the full-screen REPL shows them.</summary>
internal sealed class SubagentBridgeHook : IOrchestrationHook
{
    public Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
    {
        if (SubagentEventHook.TryRead(evt) is { } call)
            ReplJsonBridge.Emit(new { type = "tool_call", id = Guid.NewGuid().ToString("N"), name = call.Tool, summary = call.Summary, agent = call.Agent });
        return Task.CompletedTask;
    }
}
