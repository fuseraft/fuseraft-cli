using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>
/// Manages the lifecycle of MCP client connections for a single session.
///
/// <para>
/// Call <see cref="InitializeAsync"/> once after loading the config. It connects to each
/// configured MCP server, retrieves its tool list, and registers the resulting
/// <see cref="AIFunction"/> list in the supplied <see cref="PluginRegistry"/> so that agents can
/// reference the server by <see cref="McpServerConfig.Name"/> in their <c>Plugins</c> list.
/// </para>
///
/// <para>
/// Dispose the manager (via <c>await using</c>) when the session ends — it closes all
/// connections and terminates any stdio child processes gracefully.
/// </para>
/// </summary>
public sealed class McpSessionManager : IAsyncDisposable
{
    // Keyed by server name (case-insensitive) rather than a flat list so a single connection
    // can be torn down on its own via RemoveAsync — e.g. the REPL's /mcp remove — instead of
    // only ever being reachable through DisposeAsync's tear-down-everything path.
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    // The config each connected server was connected with, so it can be connected again as it was.
    private readonly Dictionary<string, McpServerConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    // What each stdio server has written to stderr lately — where servers put their logs, which would
    // otherwise be lost, though their errors often point to them.
    private readonly Dictionary<string, Queue<string>> _stderr = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of a server's latest stderr lines are kept.</summary>
    public const int KeptStderrLines = 200;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<McpSessionManager>? _logger;

    public McpSessionManager(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpSessionManager>();
    }

    /// <summary>
    /// Connects to all MCP servers listed in <paramref name="servers"/> and registers their
    /// tools as <see cref="AIFunction"/> lists in <paramref name="registry"/>.
    /// </summary>
    public async Task InitializeAsync(
        IReadOnlyList<McpServerConfig> servers,
        PluginRegistry registry,
        CancellationToken cancellationToken = default)
    {
        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException(
                    "Each MCP server entry must have a non-empty Name.");

            _logger?.LogInformation("Connecting to MCP server '{Name}' via {Transport}…",
                server.Name, server.Transport);

            var client = await ConnectAsync(server, cancellationToken);
            _clients[server.Name] = client;

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            _logger?.LogInformation("MCP server '{Name}' registered {Count} tool(s).",
                server.Name, tools.Count);

            var aiFunctions = tools.Cast<AIFunction>().ToList();
            registry.RegisterAIFunctions(server.Name, aiFunctions);
        }
    }

    /// <summary>
    /// Connects to a single MCP server and returns its client and tool list directly, without
    /// requiring a <see cref="PluginRegistry"/> — used by callers (e.g. the REPL's <c>/mcp add</c>
    /// wizard) that manage their own tool dictionary rather than a <see cref="PluginRegistry"/>
    /// instance. The connection is tracked by this manager and closed on <see cref="DisposeAsync"/>
    /// like any other, so callers should still dispose the manager when the session ends.
    /// </summary>
    public async Task<(McpClient Client, IReadOnlyList<AIFunction> Tools)> ConnectSingleAsync(
        McpServerConfig server,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server.Name))
            throw new InvalidOperationException("The MCP server entry must have a non-empty Name.");

        _logger?.LogInformation("Connecting to MCP server '{Name}' via {Transport}…",
            server.Name, server.Transport);

        var client = await ConnectAsync(server, cancellationToken);
        _clients[server.Name] = client;
        _configs[server.Name] = server;

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        _logger?.LogInformation("MCP server '{Name}' registered {Count} tool(s).",
            server.Name, tools.Count);

        return (client, tools.Cast<AIFunction>().ToList());
    }

    /// <summary>
    /// Disconnects and disposes a single server's connection (terminating its stdio child
    /// process if it has one) and stops tracking it. Returns <c>false</c> without side effects
    /// if no server with that name is connected.
    /// </summary>
    /// <summary>
    /// The latest lines a stdio server wrote to stderr, oldest first, kept even when its connect failed; null for a
    /// server that isn't stdio or was never started.
    /// </summary>
    public IReadOnlyList<string>? StderrLines(string name)
    {
        lock (_stderr)
            return _stderr.TryGetValue(name, out var lines) ? [.. lines] : null;
    }

    private void KeepStderr(Queue<string> lines, string line)
    {
        lock (_stderr)
        {
            lines.Enqueue(line);
            if (lines.Count > KeptStderrLines)
                lines.Dequeue();
        }
    }

    /// <summary>The config a connected server was connected with; null for one this manager doesn't hold.</summary>
    public McpServerConfig? ConfigOf(string name) => _configs.GetValueOrDefault(name);

    /// <summary>
    /// False once a connected server's session has ended — its process exited or its connection closed — though
    /// its tools are still registered; false too for a server this manager doesn't hold.
    /// </summary>
    public bool IsRunning(string name) => _clients.TryGetValue(name, out var client) && !client.Completion.IsCompleted;

    public async Task<bool> RemoveAsync(string name)
    {
        _configs.Remove(name);
        if (!_clients.Remove(name, out var client))
            return false;

        _logger?.LogInformation("Disconnecting MCP server '{Name}'…", name);
        await client.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? errors = null;
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        _clients.Clear();
        if (errors is not null)
            throw new AggregateException("One or more MCP clients failed to dispose.", errors);
    }

    // Private helpers

    private async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken ct)
    {
        if (server.Transport.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(server.Url))
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}': 'Url' is required for http transport.");

            var url = ProcessHelper.ExpandEnvTokens(server.Url);
            var options = new HttpClientTransportOptions
            {
                Endpoint      = new Uri(url),
                TransportMode = ParseTransportMode(server.TransportMode, server.Name),
            };

            if (server.Headers.Count > 0)
                options.AdditionalHeaders = server.Headers.ToDictionary(
                    h => h.Key, h => ProcessHelper.ExpandEnvTokens(h.Value));

            var clientOptions = new McpClientOptions();

            if (server.OAuth is { } oauth)
            {
                options.OAuth = new ClientOAuthOptions
                {
                    RedirectUri = new Uri($"http://localhost:{oauth.CallbackPort}/callback"),
                    ClientId     = oauth.ClientId,
                    ClientSecret = oauth.ClientSecret is { } secret ? ProcessHelper.ExpandEnvTokens(secret) : null,
                    Scopes       = oauth.Scopes.Count > 0 ? oauth.Scopes : null,
                    AuthorizationCallbackHandler = new McpOAuthBrowserFlow(server.Name, _logger)
                        .HandleAuthorizationUrlAsync,
                    DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "fuseraft-cli" },
                    TokenCache = new McpOAuthTokenCache(server.Name, url, ApiKeyStoreFactory.Create(), _logger),
                };

                // The default 60s initialization timeout is meant for a machine-speed handshake —
                // an interactive browser login (the user reading a consent screen, picking an
                // account, clicking "Allow") routinely takes longer, so a first-time OAuth
                // connection would otherwise fail with a spurious timeout before the user finishes.
                clientOptions.InitializationTimeout = TimeSpan.FromMinutes(5);
            }

            // The SDK's own HttpClient follows redirects with the configured headers attached; this
            // one drops them when a redirect leaves the configured origin.
            var httpClient = new HttpClient(new OriginBoundRedirectHandler(server.Headers.Keys));
            var transport  = new HttpClientTransport(options, httpClient, _loggerFactory, ownsHttpClient: true);
            return await McpClient.CreateAsync(transport, clientOptions, _loggerFactory, ct);
        }
        else // stdio (default)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}': 'Command' is required for stdio transport.");

            var stderr = new Queue<string>();
            lock (_stderr)
                _stderr[server.Name] = stderr;
            var options = new StdioClientTransportOptions
            {
                StandardErrorLines = line => KeepStderr(stderr, line),
                Command          = server.Command,
                Arguments        = server.Args.Count > 0 ? server.Args : null,
                EnvironmentVariables = server.Env.Count > 0
                    // A null value unsets that variable for the server, so it's passed on as null.
                    ? server.Env.ToDictionary(e => e.Key, e => e.Value is null ? null : ProcessHelper.ExpandEnvTokens(e.Value))
                    : null,
                WorkingDirectory = server.WorkingDirectory
            };
            var transport = new StdioClientTransport(options, _loggerFactory);
            return await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, ct);
        }
    }

    private static HttpTransportMode ParseTransportMode(string mode, string serverName) =>
        mode.Trim().ToLowerInvariant() switch
        {
            "" or "auto" or "autodetect" => HttpTransportMode.AutoDetect,
            "streamable-http" or "streamablehttp" or "streamable" => HttpTransportMode.StreamableHttp,
            "sse" => HttpTransportMode.Sse,
            _ => throw new InvalidOperationException(
                $"MCP server '{serverName}': unknown TransportMode '{mode}'. " +
                "Valid values: auto, streamable-http, sse."),
        };
}
