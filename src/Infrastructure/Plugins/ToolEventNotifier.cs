using Microsoft.Extensions.AI;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Transparent proxy that fires a <c>sub_agent_tool_call</c> event the moment a tool begins
/// executing, making sub-agent activity visible between <c>sub_agent_start</c> and
/// <c>sub_agent_end</c> in the event log.
/// </summary>
internal sealed class ToolEventNotifier(AIFunction inner, EventEmitter emitter, string? agentName)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        await emitter.EmitAsync("sub_agent_tool_call",
            agent:   agentName,
            payload: new { tool = Name, args = SummarizeArgs(arguments) });
        return await InnerFunction.InvokeAsync(arguments, cancellationToken);
    }

    private static string? SummarizeArgs(AIFunctionArguments? args)
    {
        if (args is null) return null;
        ReadOnlySpan<string> priority = ["path", "command", "script", "url", "key", "query", "message", "branch"];
        foreach (var key in priority)
        {
            var match = args.FirstOrDefault(kv =>
                string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                var val = match.Value.ToString() ?? string.Empty;
                return $"{key}={System.Net.WebUtility.HtmlDecode(val.Length > 60 ? val[..60] : val)}";
            }
        }
        var first = args.FirstOrDefault();
        if (first.Value is null) return null;
        var fv = first.Value.ToString() ?? string.Empty;
        return $"{first.Key}={System.Net.WebUtility.HtmlDecode(fv.Length > 60 ? fv[..60] : fv)}";
    }
}
