using System.Text.Json;
using fuseraft.Core;
using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// Persists MCP servers added via the REPL's <c>/mcp add</c> wizard so they reconnect
/// automatically on the next <c>fuseraft repl</c> launch, without re-running the wizard.
/// Deliberately a separate file from <see cref="UserConfigStore"/> — that store's schema
/// (model/provider/API key) is unrelated and already carries legacy-field migration logic
/// that a list-shaped addition would only complicate.
/// </summary>
public static class ReplMcpServerStore
{
    public static string StorePath => Path.Combine(FuseraftPaths.GlobalRoot, "repl-mcp-servers.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static List<McpServerConfig> Load()
    {
        if (!File.Exists(StorePath)) return [];
        try
        {
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<List<McpServerConfig>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(List<McpServerConfig> servers)
    {
        Directory.CreateDirectory(FuseraftPaths.GlobalRoot);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(servers, JsonOptions));
    }
}
