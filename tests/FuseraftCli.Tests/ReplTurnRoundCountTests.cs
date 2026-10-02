using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// A turn's <c>tool_rounds</c> is the number of model calls it made. Counted from the stream it was
/// inflated: Microsoft.Extensions.AI's OpenAI adapter repeats the finish reason on the usage-only
/// update and on the tool-call update, so one call to xAI showed two or three of them (25 real calls
/// reported as 71). These turns run through the real client chain (<see cref="ReplFactory.BuildClient"/>)
/// against a local server replaying streams captured from grok-4.5.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnRoundCountTests : IDisposable
{
    // Captured from api.x.ai (grok-4.5, stream_options.include_usage), trimmed to the fields that matter.
    private const string ToolCallStream = """
        data: {"id":"c6d1c3a3","object":"chat.completion.chunk","created":1790917839,"model":"grok-4.5","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"id":"call-0","function":{"name":"get_time","arguments":"{}"},"index":0,"type":"function"}]}}]}

        data: {"id":"c6d1c3a3","object":"chat.completion.chunk","created":1790917839,"model":"grok-4.5","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"c6d1c3a3","object":"chat.completion.chunk","created":1790917839,"model":"grok-4.5","choices":[],"usage":{"prompt_tokens":570,"completion_tokens":7,"total_tokens":618,"prompt_tokens_details":{"cached_tokens":384}}}

        data: [DONE]


        """;

    private const string TextStream = """
        data: {"id":"3b4c899a","object":"chat.completion.chunk","created":1790917838,"model":"grok-4.5","choices":[{"index":0,"delta":{"content":"It is 12:00.","role":"assistant"}}]}

        data: {"id":"3b4c899a","object":"chat.completion.chunk","created":1790917838,"model":"grok-4.5","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"3b4c899a","object":"chat.completion.chunk","created":1790917838,"model":"grok-4.5","choices":[],"usage":{"prompt_tokens":499,"completion_tokens":1,"total_tokens":511,"prompt_tokens_details":{"cached_tokens":384}}}

        data: [DONE]


        """;

    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];
    private readonly List<HttpListener> _listeners = [];

    public ReplTurnRoundCountTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
        foreach (var l in _listeners) l.Close();
        foreach (var ctx in _contexts) { ctx.Emitter.Dispose(); ctx.Factory.Dispose(); }
        foreach (var path in _eventsPaths) if (File.Exists(path)) File.Delete(path);
    }

    // Answers each chat-completions request with the next stream in turn (the last one repeats).
    private string Serve(params string[] streams)
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listeners.Add(listener);
        var next = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                var body = Encoding.UTF8.GetBytes(streams[Math.Min(next++, streams.Length - 1)]);
                c.Response.ContentType = "text/event-stream";
                await c.Response.OutputStream.WriteAsync(body);
                c.Response.Close();
            }
        });
        return $"http://127.0.0.1:{port}/v1";
    }

    private (ReplSessionContext Ctx, string EventsPath) NewContext(string endpoint)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        _eventsPaths.Add(eventsPath);
        var getTime = AIFunctionFactory.Create(() => "12:00", "get_time", "Get the time");
        var model   = new ModelConfig { ModelId = "grok-4.5", Provider = "openai", ApiKey = "test", Endpoint = endpoint };
        var factory = new ChatClientFactory();
        var tracker = new AdaptiveTrimTracker();
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "round-count-session", startedAt: DateTime.UtcNow,
            modelId: model.ModelId, modelConfig: model, userCfg: null,
            client: ReplFactory.BuildClient(model, factory, addFunctionInvocation: true, tracker, tools: [getTime]),
            factory: factory, keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath), eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: new() { ["Test"] = [getTime] }, systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: tracker);
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return (ctx, eventsPath);
    }

    private static async Task<JsonElement> TurnEndAsync(string eventsPath)
    {
        foreach (var line in await File.ReadAllLinesAsync(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("event_type").GetString() == "turn_end")
                return doc.RootElement.GetProperty("payload").Clone();
        }
        throw new InvalidOperationException("no turn_end event");
    }

    [Fact]
    public async Task ToolCallThenAnswer_IsTwoRounds()
    {
        var (ctx, eventsPath) = NewContext(Serve(ToolCallStream, TextStream));

        await ReplTurn.ExecuteAsync(
            ctx, "What time is it?", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var end = await TurnEndAsync(eventsPath);
        Assert.Equal(2, end.GetProperty("tool_rounds").GetInt32());   // was 5: 3 finish reasons + 2
        Assert.Equal(570 + 499, end.GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public async Task SingleAnswer_IsOneRound()
    {
        var (ctx, eventsPath) = NewContext(Serve(TextStream));

        await ReplTurn.ExecuteAsync(
            ctx, "Say it is noon.", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.Equal(1, (await TurnEndAsync(eventsPath)).GetProperty("tool_rounds").GetInt32());   // was 2
    }

    // A step turn's round cap is 5, so the inflated count told the user a two-call step had "reached
    // the 5-round limit; later calls in this step may have been cut short".
    [Fact]
    public async Task TwoCallStep_DoesNotReportHittingTheStepRoundCap()
    {
        var (ctx, eventsPath) = NewContext(Serve(ToolCallStream, TextStream));

        await ReplTurn.ExecuteAsync(
            ctx, "Get the time.", isStepRequest: true, capturePlan: false,
            activeStep: new PlanStep(1, "Get the time", "get_time", null), CancellationToken.None, stepTotal: 1);

        var end = await TurnEndAsync(eventsPath);
        Assert.Equal(2, end.GetProperty("tool_rounds").GetInt32());
        Assert.False(end.GetProperty("hit_iteration_cap").GetBoolean());
    }

    [Fact]
    public async Task CallCounter_CountsEachModelCallUnderTheToolLoop()
    {
        var endpoint = Serve(ToolCallStream, TextStream);
        var getTime  = AIFunctionFactory.Create(() => "12:00", "get_time", "Get the time");
        using var factory = new ChatClientFactory();
        var client = ReplFactory.BuildClient(
            new ModelConfig { ModelId = "grok-4.5", Provider = "openai", ApiKey = "test", Endpoint = endpoint },
            factory, addFunctionInvocation: true, new AdaptiveTrimTracker(), tools: [getTime]);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "time?")], new ChatOptions { Tools = [getTime] })) { }

        Assert.Equal(2, client.GetService<ModelCallCountingChatClient>()?.Calls);
    }
}
