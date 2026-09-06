using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using fuseraft.Core.Models;
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
    public async Task<bool> RemoveAsync(string name)
    {
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

            var options = new HttpClientTransportOptions { Endpoint = new Uri(server.Url) };
            var transport = new HttpClientTransport(options, _loggerFactory);
            return await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, ct);
        }
        else // stdio (default)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}': 'Command' is required for stdio transport.");

            var options = new StdioClientTransportOptions
            {
                Command          = server.Command,
                Arguments        = server.Args.Count > 0 ? server.Args : null,
                EnvironmentVariables = server.Env.Count > 0 ? server.Env : null,
                WorkingDirectory = server.WorkingDirectory
            };
            var transport = new StdioClientTransport(options, _loggerFactory);
            return await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, ct);
        }
    }
}
