using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Models;

namespace fuseraft.Cli.DevUI;

/// <summary>
/// Lightweight ASP.NET Core server that serves a real-time session visualization page.
/// Exposes:
///   GET /            — self-contained HTML page
///   GET /api/stream  — Server-Sent Events stream of session events
///
/// New clients receive the full event history on connect so a page refresh always shows
/// the complete session. Events are JSON objects with a "type" discriminator field.
/// </summary>
public sealed class DevUIServer : IAsyncDisposable
{
    private const int MaxHistoryEvents = 1_000;
    private readonly Lock _lock = new();
    private readonly List<string>              _history = [];
    private readonly List<ChannelWriter<string>> _clients = [];

    private WebApplication? _app;

    public int Port { get; private set; }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder([]);
        // Bind to port 0 so the OS assigns a free port atomically — avoids TOCTOU race.
        builder.WebHost.UseUrls("http://localhost:0");
        builder.Logging.ClearProviders(); // suppress Kestrel startup noise

        _app = builder.Build();

        _app.MapGet("/", () =>
            Results.Content(DevUIHtml.Page, "text/html; charset=utf-8"));

        _app.MapGet("/api/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType    = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var ch = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true });

            string[] snapshot;
            lock (_lock)
            {
                snapshot = [.. _history];
                _clients.Add(ch.Writer);
            }

            var ct = ctx.RequestAborted;
            try
            {
                // Replay full history so refreshing the page shows the whole session.
                foreach (var item in snapshot)
                {
                    await ctx.Response.WriteAsync($"data: {item}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }

                // Stream live events as they arrive.
                await foreach (var item in ch.Reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"data: {item}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                lock (_lock) _clients.Remove(ch.Writer);
                ch.Writer.TryComplete();
            }
        });

        await _app.StartAsync(cancellationToken);
        Port = new Uri(_app.Urls.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            foreach (var w in _clients) w.TryComplete();
            _clients.Clear();
        }

        if (_app is not null)
            await _app.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Broadcast helpers called from RunCommand
    // -------------------------------------------------------------------------

    public void BroadcastSessionStart(string sessionId, string task, string configName)
        => Emit("session_start", new { sessionId, task, configName, ts = Ts() });

    public void BroadcastAgentStarting(string agentName)
        => Emit("agent_starting", new { agentName, ts = Ts() });

    public void BroadcastMessage(AgentMessage msg, TimeSpan elapsed)
        => Emit("message", new
        {
            agentName    = msg.AgentName,
            content      = msg.Content,
            turnIndex    = msg.TurnIndex,
            role         = msg.Role,
            inputTokens  = msg.Usage?.InputTokens,
            outputTokens = msg.Usage?.OutputTokens,
            elapsedMs    = (long)elapsed.TotalMilliseconds,
            ts           = Ts(),
        });

    public void BroadcastSessionEnd(bool succeeded, string? errorMessage = null)
        => Emit("session_end", new { succeeded, errorMessage, ts = Ts() });

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void Emit(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(
            new { type = eventType, data }, _json);

        lock (_lock)
        {
            _history.Add(json);
            if (_history.Count > MaxHistoryEvents)
                _history.RemoveAt(0);
            foreach (var w in _clients)
                w.TryWrite(json);
        }
    }

    private static string Ts() => DateTimeOffset.UtcNow.ToString("O");
}
