using System.Text.Json.Nodes;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Tui;

/// <summary>Shows a subagent's tool calls in the full-screen REPL, nested under the call that started it and tagged with its name.</summary>
internal sealed class SubagentEventHook(IReplScreen screen) : IOrchestrationHook
{
    public Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt.EventType == EventTypes.SubagentToolCall && Get(evt.Payload, "tool") is { } tool)
        {
            var agent = Get(evt.Payload, "label") ?? evt.Agent ?? "subagent";
            var args = (Get(evt.Payload, "args") ?? "").ReplaceLineEndings(" ").Trim();
            screen.ToolStarted(Guid.NewGuid().ToString("N"), tool, args.Length > 100 ? args[..97] + "..." : args, agent);
        }
        return Task.CompletedTask;
    }

    // A payload is an anonymous object, or a JSON object when the subagent runs in a parallel batch.
    static string? Get(object? payload, string name) => payload switch
    {
        null => null,
        JsonObject json => json[name]?.ToString(),
        _ => payload.GetType().GetProperty(name)?.GetValue(payload)?.ToString(),
    };
}
