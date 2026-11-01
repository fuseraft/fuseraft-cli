using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace fuseraft.Cli.Serve;

/// <summary>
/// How <c>fuseraft serve</c>'s HTTP listener binds; <see cref="Token"/> comes from <see cref="ServeHttpHost.TokenVariable"/>,
/// <see cref="TokensFile"/> from <c>--tokens-file</c>, and with a <see cref="Certificate"/> it serves HTTPS and <c>wss://</c>.
/// </summary>
public sealed record ServeHttpOptions(
    string Bind, int Port, string? Token, X509Certificate2? Certificate = null, string? TokensFile = null)
{
    public bool Secure => Certificate is not null;

    /// <summary>Throws <see cref="InvalidOperationException"/> when <see cref="TokensFile"/> can't be used.</summary>
    public ServeTokens CreateTokens() => new(Token, TokensFile);
}

/// <summary>
/// The daemon's HTTP listener: MCP at <c>/mcp</c> for other agents, and the attach protocol over
/// a WebSocket at <c>/attach</c> for clients that can't reach the Unix socket (another machine,
/// or a host such as fuseraft-web). With a token set, both require <c>Authorization: Bearer</c>.
/// </summary>
public static class ServeHttpHost
{
    public const string TokenVariable = "FUSERAFT_SERVE_TOKEN";
    public const string AttachPath    = "/attach";

    private const string DispatchToolName = "dispatch_task";

    // Pings find a client that vanished without closing (a dropped network, a suspended laptop),
    // so its task's approvals move on to its reconnect grace instead of waiting on a dead socket.
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan KeepAliveTimeout  = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "[::1]",
    };

    /// <summary>Null when the options are usable, otherwise why not.</summary>
    public static string? Validate(ServeHttpOptions options)
    {
        if (options.Port is < 0 or > 65535) return $"--http-port must be between 0 and 65535, got {options.Port}.";
        ServeTokens tokens;
        try { tokens = options.CreateTokens(); }
        catch (InvalidOperationException ex) { return ex.Message; }
        return !ServeA2AHost.IsLoopback(options.Bind) && !tokens.Any
            ? $"--bind {options.Bind} accepts connections from other machines, so clients must authenticate. " +
              $"Set {TokenVariable} to a secret, or give each client its own with --tokens-file; clients send it as 'Authorization: Bearer <token>'."
            : null;
    }

    /// <param name="attach">Runs one attach connection over a line stream, as the client it says it is, until it ends.</param>
    public static WebApplication Build(
        ServeHttpOptions options, ServeTaskQueue queue, Func<Stream, AttachIdentity, CancellationToken, Task> attach)
    {
        var tokens = options.CreateTokens();
        var mcpTools = new ServeMcpTools(queue);
        var toolNames = new (string Method, string ToolName)[]
        {
            (nameof(ServeMcpTools.DispatchTask), DispatchToolName),
            (nameof(ServeMcpTools.GetStatus),    "get_status"),
            (nameof(ServeMcpTools.GetResult),    "get_result"),
        };
        var tools = toolNames.Select(t =>
        {
            var method = typeof(ServeMcpTools).GetMethod(t.Method)!;
            var function = AIFunctionFactory.Create(method, mcpTools, new AIFunctionFactoryOptions { Name = t.ToolName });
            return McpServerTool.Create(function);
        });

        var builder = WebApplication.CreateSlimBuilder([]);
        ServeTls.Listen(builder.WebHost, ServeA2AHost.UrlHost(options.Bind), options.Port, options.Certificate);
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools).WithRequestFilters(filters =>
        {
            // A read-only token watches: it can ask about tasks but not dispatch one.
            filters.AddListToolsFilter(next => async (ctx, ct) =>
            {
                var result = await next(ctx, ct);
                if (ServeTokens.IsReadOnly(ctx.User)) result.Tools = [.. result.Tools.Where(t => t.Name != DispatchToolName)];
                return result;
            });
            filters.AddCallToolFilter(next => async (ctx, ct) =>
                ctx.Params?.Name == DispatchToolName && ServeTokens.IsReadOnly(ctx.User)
                    ? new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = "This token is read-only: it can check on tasks, but not dispatch them." }],
                    }
                    : await next(ctx, ct));
        });

        var app = builder.Build();
        // On loopback with no token, the Host check is what stops a web page that rebinds its
        // own domain to 127.0.0.1 from reaching the daemon.
        if (ServeA2AHost.IsLoopback(options.Bind))
            app.Use(RequireLoopbackHost);
        if (tokens.Any)
            app.Use(tokens.Require());

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = KeepAliveInterval, KeepAliveTimeout = KeepAliveTimeout });
        app.Map(AttachPath, (HttpContext ctx) => AcceptAttachAsync(ctx, tokens, attach));
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>The address a client on this machine would use for <paramref name="path"/>, over HTTP or a WebSocket.</summary>
    public static string LocalUrl(ServeHttpOptions options, int boundPort, bool webSocket, string path) =>
        $"{(webSocket ? options.Secure ? "wss" : "ws" : options.Secure ? "https" : "http")}://{ServeA2AHost.LocalUrlHost(options.Bind)}:{boundPort}{path}";

    /// <summary>How often an open attach connection checks that its token wasn't revoked from --tokens-file.</summary>
    internal static TimeSpan RevocationCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    private static async Task DropWhenRevokedAsync(ServeTokens tokens, ServeToken token, System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RevocationCheckInterval, ct);
                if (tokens.IsValid(token)) continue;
                socket.Abort();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task RequireLoopbackHost(HttpContext ctx, RequestDelegate next)
    {
        if (!LoopbackHosts.Contains(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        await next(ctx);
    }

    private static async Task AcceptAttachAsync(
        HttpContext ctx, ServeTokens tokens, Func<Stream, AttachIdentity, CancellationToken, Task> attach)
    {
        // WebSockets have no CORS: any page the user opens could connect. Browsers always send
        // Origin on a WebSocket handshake and the attach clients never do, so refuse it outright.
        if (ctx.Request.Headers.Origin.Count > 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Expected a WebSocket upgrade.");
            return;
        }
        string? clientId = ctx.Request.Headers[ServeAttachClients.ClientIdHeader];
        if (clientId is not null && !ServeAttachClients.IsValidId(clientId))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync($"{ServeAttachClients.ClientIdHeader} must be 1-64 letters, digits, '-' or '_'.");
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        // The connection loop waits on reads that don't take a token; aborting ends them.
        using var abortOnEnd = ended.Token.Register(socket.Abort);
        var token = ServeTokens.Of(ctx);
        if (token is not null) _ = DropWhenRevokedAsync(tokens, token, socket, ended.Token);
        // Owning the socket lets the attach handler close it when a newer connection replaces this one.
        await using var stream = new WebSocketLineStream(socket, ownsSocket: true);
        await attach(stream, new AttachIdentity(clientId, token?.Name, token?.ReadOnly ?? false), ended.Token);

        if (socket.State is System.Net.WebSockets.WebSocketState.Open or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try { await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (System.Net.WebSockets.WebSocketException) { }
        }
    }
}
