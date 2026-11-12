using System.Text.Json;
using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// MCP servers handed to fuseraft by whatever launched it (fuseraft-web's server-wide MCP servers) through the
/// <c>FUSERAFT_MCP_SERVERS</c> environment variable: a JSON array of <see cref="McpServerConfig"/>. They join the
/// REPL's saved servers and an orchestration config's <c>McpServers</c>, and are never written to either.
/// </summary>
public static class ManagedMcpServers
{
    public const string EnvVar = "FUSERAFT_MCP_SERVERS";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The servers in <see cref="EnvVar"/>; <paramref name="error"/> says why it couldn't be read.</summary>
    public static List<McpServerConfig> Load(out string? error) =>
        Parse(Environment.GetEnvironmentVariable(EnvVar), out error);

    internal static List<McpServerConfig> Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var servers = JsonSerializer.Deserialize<List<McpServerConfig?>>(json, Json) ?? [];
            var named   = servers.OfType<McpServerConfig>().Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
            if (named.Count < servers.Count)
                error = $"{EnvVar} has {servers.Count - named.Count} server(s) without a Name; they were skipped.";
            return named;
        }
        catch (JsonException ex)
        {
            error = $"{EnvVar} isn't a JSON array of MCP servers ({ex.Message}); none of its servers were connected.";
            return [];
        }
    }

    /// <summary>
    /// <paramref name="own"/> followed by the managed servers whose names it doesn't already use, so a config or a
    /// user's saved server keeps a name it chose.
    /// </summary>
    public static List<McpServerConfig> Merge(IEnumerable<McpServerConfig> own, IEnumerable<McpServerConfig> managed)
    {
        var result = own.ToList();
        var names  = new HashSet<string>(result.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        result.AddRange(managed.Where(s => names.Add(s.Name)));
        return result;
    }

    /// <summary>The managed servers an orchestration's agents name in <c>Plugins</c>, unless its own <c>McpServers</c> uses the name.</summary>
    public static List<McpServerConfig> ReferencedBy(IEnumerable<McpServerConfig> managed,
        IEnumerable<McpServerConfig> own, IEnumerable<string> plugins)
    {
        var used  = new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(own.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        return [.. managed.Where(s => used.Contains(s.Name) && taken.Add(s.Name))];
    }
}
