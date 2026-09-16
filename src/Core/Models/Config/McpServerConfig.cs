namespace fuseraft.Core.Models.Config;

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
    /// HTTP(S) endpoint URL (http transport only). E.g. <c>"https://mcp.example.com/mcp"</c>.
    /// Supports <c>${ENV_VAR}</c> tokens, expanded at load time.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Additional HTTP headers sent with every request (http transport only) — e.g. a static
    /// API key or bearer token: <c>{ "Authorization": "Bearer ${MY_SERVER_TOKEN}" }</c>.
    /// Values support <c>${ENV_VAR}</c> tokens, expanded at load time, so secrets stay out of
    /// the config file itself. Ignored when <see cref="OAuth"/> is set for the same header name.
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>
    /// HTTP transport mode (http transport only): <c>"auto"</c> (default; probes the endpoint
    /// and picks Streamable HTTP or legacy SSE), <c>"streamable-http"</c>, or <c>"sse"</c>. Pin
    /// this explicitly for servers behind a proxy where auto-detection is unreliable, or for a
    /// server that only implements one of the two transports.
    /// </summary>
    public string TransportMode { get; init; } = "auto";

    /// <summary>
    /// Enables interactive OAuth 2.1 login for this server (http transport only). When set, the
    /// first connection opens the user's browser for authorization; the resulting tokens are
    /// cached in the OS keychain (keyed by server name) so later sessions reconnect silently
    /// until the token is revoked or expires without a refresh token. Leave unset for servers
    /// that don't require OAuth, or that use a static <see cref="Headers"/> token instead.
    /// </summary>
    public McpOAuthConfig? OAuth { get; init; }
}

/// <summary>
/// OAuth 2.1 client options for an <see cref="McpServerConfig"/> using the http transport.
/// Most fields are optional — servers that support dynamic client registration (the common
/// case for MCP) need no <see cref="ClientId"/>/<see cref="ClientSecret"/> at all.
/// </summary>
public record McpOAuthConfig
{
    /// <summary>
    /// Pre-registered OAuth client ID. Omit to use dynamic client registration (RFC 7591),
    /// which fuseraft-cli requests automatically when the server supports it.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Pre-registered OAuth client secret, paired with <see cref="ClientId"/>. Supports
    /// <c>${ENV_VAR}</c> tokens, expanded at load time.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// OAuth scopes to request. Omit to accept the server's default scope set.
    /// </summary>
    public List<string> Scopes { get; init; } = [];

    /// <summary>
    /// Loopback port used for the local OAuth redirect callback during login
    /// (<c>http://localhost:{CallbackPort}/callback</c>). Change this only if the default
    /// collides with something else on the machine.
    /// </summary>
    public int CallbackPort { get; init; } = 1179;
}
