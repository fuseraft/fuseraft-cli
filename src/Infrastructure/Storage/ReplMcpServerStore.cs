using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// Persists MCP servers added via the REPL's <c>/mcp add</c> wizard so they reconnect
/// automatically on the next <c>fuseraft repl</c> launch, without re-running the wizard.
/// Backed by the <c>mcpServers</c> section of the shared <c>~/.fuseraft/config</c> file (see
/// <see cref="UserConfigStore"/>) — a thin wrapper so <c>/mcp add</c>/<c>/mcp remove</c> don't
/// need to know about the rest of that file's shape.
/// </summary>
public static class ReplMcpServerStore
{
    public static string StorePath => UserConfigStore.ConfigPath;

    public static List<McpServerConfig> Load() =>
        UserConfigStore.Load().Config?.McpServers ?? [];

    public static void Save(List<McpServerConfig> servers)
    {
        var config = UserConfigStore.Load().Config ?? new UserConfig();
        config.McpServers = servers;
        UserConfigStore.Save(config);
    }
}
