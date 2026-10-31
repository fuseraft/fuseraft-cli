using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace fuseraft.Cli.Serve;

/// <summary>How <c>fuseraft serve</c>'s HTTP listener binds; <see cref="Token"/> comes from <see cref="ServeHttpHost.TokenVariable"/>.</summary>
public sealed record ServeHttpOptions(string Bind, int Port, string? Token);

/// <summary>
/// The daemon's HTTP listener: MCP at <c>/mcp</c> for other agents, and the attach protocol over
/// a WebSocket at <c>/attach</c> for clients that can't reach the Unix socket (another machine,
/// or a host such as fuseraft-server). With a token set, both require <c>Authorization: Bearer</c>.
/// </summary>
public static class ServeHttpHost
{
    public const string TokenVariable = "FUSERAFT_SERVE_TOKEN";
    public const string AttachPath    = "/attach";

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "[::1]",
    };

    /// <summary>Null when the options are usable, otherwise why not.</summary>
    public static string? Validate(ServeHttpOptions options) =>
        options.Port is < 0 or > 65535
            ? $"--http-port must be between 0 and 65535, got {options.Port}."
            : !ServeA2AHost.IsLoopback(options.Bind) && string.IsNullOrEmpty(options.Token)
                ? $"--bind {options.Bind} accepts connections from other machines, so clients must authenticate. " +
                  $"Set {TokenVariable} to a secret; clients send it as 'Authorization: Bearer <token>'."
                : null;

    /// <param name="attach">Runs one attach connection over a line stream until it ends.</param>
    public static WebApplication Build(
        ServeHttpOptions options, ServeTaskQueue queue, Func<Stream, CancellationToken, Task> attach)
    {
        var mcpTools = new ServeMcpTools(queue);
        var toolNames = new (string Method, string ToolName)[]
        {
            (nameof(ServeMcpTools.DispatchTask), "dispatch_task"),
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
        builder.WebHost.UseUrls($"http://{ServeA2AHost.UrlHost(options.Bind)}:{options.Port}");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);

        var app = builder.Build();
        // On loopback with no token, the Host check is what stops a web page that rebinds its
        // own domain to 127.0.0.1 from reaching the daemon.
        if (ServeA2AHost.IsLoopback(options.Bind))
            app.Use(RequireLoopbackHost);
        if (!string.IsNullOrEmpty(options.Token))
            app.Use(ServeA2AHost.RequireBearer(options.Token));

        app.UseWebSockets();
        app.Map(AttachPath, (HttpContext ctx) => AcceptAttachAsync(ctx, attach));
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>The address a client on this machine would use for <paramref name="path"/>.</summary>
    public static string LocalUrl(ServeHttpOptions options, int boundPort, string scheme, string path) =>
        $"{scheme}://{ServeA2AHost.LocalUrlHost(options.Bind)}:{boundPort}{path}";

    private static async Task RequireLoopbackHost(HttpContext ctx, RequestDelegate next)
    {
        if (!LoopbackHosts.Contains(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        await next(ctx);
    }

    private static async Task AcceptAttachAsync(HttpContext ctx, Func<Stream, CancellationToken, Task> attach)
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

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        // The connection loop waits on reads that don't take a token; aborting ends them.
        using var abortOnEnd = ended.Token.Register(socket.Abort);
        await using var stream = new WebSocketLineStream(socket);
        await attach(stream, ended.Token);

        if (socket.State is System.Net.WebSockets.WebSocketState.Open or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try { await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (System.Net.WebSockets.WebSocketException) { }
        }
    }
}
