namespace fuseraft.Core.Models;

/// <summary>
/// Describes a single MCP (Model Context Protocol) server to connect to at session startup.
/// The server's tools are registered under <see cref="Name"/> and can be referenced
/// from any agent's <c>Plugins</c> list.
/// </summary>
public record McpServerConfig
{
    /// <summary>
    /// Plugin name used to reference this server in agent configs (e.g. <c>"MyMcpServer"</c>).
    /// Must be unique across built-in plugins and other MCP servers.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Transport type: <c>"stdio"</c> (default) or <c>"http"</c>.
    /// </summary>
    public string Transport { get; init; } = "stdio";

    // stdio options

    /// <summary>
    /// Executable to launch (stdio transport only). E.g. <c>"npx"</c>, <c>"python"</c>.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Arguments passed to <see cref="Command"/> (stdio transport only).
    /// </summary>
    public List<string> Args { get; init; } = [];

    /// <summary>
    /// Additional environment variables set for the server process (stdio transport only).
    /// A null value removes the variable from the child process environment.
    /// </summary>
    public Dictionary<string, string?> Env { get; init; } = [];

    /// <summary>
    /// Working directory for the server process (stdio transport only). Defaults to the
    /// current directory when omitted.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    // HTTP / SSE options

    /// <summary>
    /// SSE endpoint URL (http transport only). E.g. <c>"http://localhost:3000/sse"</c>.
    /// </summary>
    public string? Url { get; init; }
}
