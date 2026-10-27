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
        if (TryRead(evt) is { } call)
            screen.ToolStarted(Guid.NewGuid().ToString("N"), call.Tool, call.Summary, call.Agent);
        return Task.CompletedTask;
    }

    /// <summary>The tool a subagent called, a one-line summary of its arguments, and the subagent's name; null for any other event.</summary>
    internal static (string Tool, string Summary, string Agent)? TryRead(OrchestrationEvent evt)
    {
        if (evt.EventType != EventTypes.SubagentToolCall || Get(evt.Payload, "tool") is not { } tool) return null;
        var agent = Get(evt.Payload, "label") ?? evt.Agent ?? "subagent";
        var args = (Get(evt.Payload, "args") ?? "").ReplaceLineEndings(" ").Trim();
        return (tool, args.Length > 100 ? args[..97] + "..." : args, agent);
    }

    // A payload is an anonymous object, or a JSON object when the subagent runs in a parallel batch.
    static string? Get(object? payload, string name) => payload switch
    {
        null => null,
        JsonObject json => json[name]?.ToString(),
        _ => payload.GetType().GetProperty(name)?.GetValue(payload)?.ToString(),
    };
}
