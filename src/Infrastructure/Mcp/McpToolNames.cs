using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>
/// Keeps tool names unique when MCP servers bring tools named like another server's or a built-in tool: providers
/// reject a request whose tools share a name, which would fail every turn.
/// </summary>
public static partial class McpToolNames
{
    /// <summary>The longest tool name providers accept (Anthropic and OpenAI both allow 64).</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The tools as the model should see them. A built-in tool (<c>Server</c> null) keeps its name; an MCP tool
    /// whose name another tool also has is offered as <c>&lt;server&gt;_&lt;name&gt;</c>, and still calls its own.
    /// </summary>
    public static List<AIFunction> Disambiguate(IReadOnlyList<(string? Server, AIFunction Tool)> tools)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, tool) in tools)
            counts[tool.Name] = counts.GetValueOrDefault(tool.Name) + 1;
        if (counts.Values.All(c => c == 1))
            return [.. tools.Select(t => t.Tool)];

        bool Clashes((string? Server, AIFunction Tool) t) => t.Server is not null && counts[t.Tool.Name] > 1;

        var taken = tools.Where(t => !Clashes(t)).Select(t => t.Tool.Name).ToHashSet(StringComparer.Ordinal);
        var result = new List<AIFunction>(tools.Count);
        foreach (var t in tools)
        {
            if (!Clashes(t))
            {
                result.Add(t.Tool);
                continue;
            }
            var name = Unique($"{Unsafe().Replace(t.Server!, "_")}_{t.Tool.Name}", taken);
            taken.Add(name);
            result.Add(new RenamedFunction(t.Tool, name));
        }
        return result;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = Fit(name, "");
        for (var n = 2; taken.Contains(candidate); n++)
            candidate = Fit(name, $"_{n}");
        return candidate;
    }

    private static string Fit(string name, string suffix) =>
        (name.Length + suffix.Length <= MaxLength ? name : name[..(MaxLength - suffix.Length)]) + suffix;

    // Server names come from config and may hold characters tool names can't.
    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();

    // Only the name changes: calls, schema and GetService (approval gates, read-only hints) go to the tool itself.
    private sealed class RenamedFunction(AIFunction inner, string name) : DelegatingAIFunction(inner)
    {
        public override string Name => name;
    }
}
