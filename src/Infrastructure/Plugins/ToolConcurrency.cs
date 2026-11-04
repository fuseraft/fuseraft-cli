using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>Which tool calls may overlap when a model asks for several in one response.</summary>
internal static class ToolConcurrency
{
    /// <summary>Lane of a read that overlaps with every other read.</summary>
    public const string AnyLane = "";

    // FileSystemPlugin's per-turn read record, cache hints, read budget and the session read cache
    // are plain collections, so its reads overlap with other tools but run one at a time among themselves.
    private const string FileSystemLane = "filesystem";

    private static readonly IReadOnlySet<string> OtherReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "search_content", "search_callers", "search_symbol",
        "shell_get_env", "shell_which", "shell_get_working_directory",
        "http_get", "http_head",
        "subagent_explore", "subagent_locate", "subagent_explore_many",
    };

    /// <summary>
    /// The lane a read-only tool runs in, or null when the tool can change something and must run
    /// alone. An MCP tool counts as read-only only when its server marks it so.
    /// </summary>
    public static string? ReadLane(AIFunction function)
    {
        if (function.GetService<McpClientTool>() is { } mcp)
            return mcp.ProtocolTool.Annotations?.ReadOnlyHint == true ? AnyLane : null;

        var name = function.Name;
        if (ExplorerToolSets.FileSystemRead.Contains(name)) return FileSystemLane;
        return ExplorerToolSets.GitRead.Contains(name) || OtherReads.Contains(name) ? AnyLane : null;
    }

    /// <summary>
    /// Wraps the tools named in <paramref name="names"/> so that only one of them runs at a time —
    /// for commands that parallel subagents share (two builds in one tree would trip over each other).
    /// </summary>
    public static List<AIFunction> OneAtATime(IEnumerable<AIFunction> tools, IReadOnlySet<string> names)
    {
        var gate = new SemaphoreSlim(1, 1);
        return [.. tools.Select(t => names.Contains(t.Name) ? new GatedFunction(t, gate) : t)];
    }

    private sealed class GatedFunction(AIFunction inner, SemaphoreSlim gate) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try { return await base.InvokeCoreAsync(arguments, cancellationToken); }
            finally { gate.Release(); }
        }
    }
}
