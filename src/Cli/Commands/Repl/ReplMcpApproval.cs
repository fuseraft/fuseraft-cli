using fuseraft.Cli.Tui;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Puts an MCP server's tools behind the same HITL approval as the mutating Git and Http tools. A tool the server
/// marks read-only (MCP's <c>readOnlyHint</c>) is left as it is, as read-only built-in tools are; any other call
/// asks first, showing its arguments. Approval is for the action <c>server.tool</c>, so "always" covers that one tool.
/// </summary>
internal static class ReplMcpApproval
{
    public static List<AIFunction> Gate(string server, IEnumerable<AIFunction> tools, Func<string, string, Task<bool>>? approve) =>
        approve is null ? [.. tools] : [.. tools.Select(t => IsReadOnly(t) ? t : new GatedTool(t, server, approve))];

    /// <summary>
    /// True for an MCP tool its server marks read-only, looking through the wrappers the REPL puts around every
    /// tool (see <see cref="ReplSessionContext.WrapTool"/>). A tool behind the approval gate never is.
    /// </summary>
    internal static bool IsReadOnly(AIFunction tool) =>
        tool.GetService<GatedTool>() is null &&
        tool.GetService<McpClientTool>() is { ProtocolTool.Annotations.ReadOnlyHint: true };

    private sealed class GatedTool(AIFunction inner, string server, Func<string, string, Task<bool>> approve)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var detail = TuiRenderer.FormatArguments(arguments);
            if (!await approve($"{server}.{Name}", detail.Length == 0 ? "(no arguments)" : detail))
                return PluginResult.Denied($"MCP tool '{Name}' on '{server}' blocked by user.");
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
