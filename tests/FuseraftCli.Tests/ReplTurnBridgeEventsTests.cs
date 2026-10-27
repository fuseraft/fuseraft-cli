using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// The VS Code panel draws a turn the way the full-screen REPL does — thinking, each tool call with its result, each
/// changed file with its line counts, messages sent mid-turn — from bridge events the turn emits in JSON mode.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnBridgeEventsTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-bridge-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];
    private readonly TextWriter _originalWriter = ReplJsonBridge.Writer;
    private readonly StringWriter _events = new();

    public ReplTurnBridgeEventsTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);
        ReplJsonBridge.Writer = _events;
    }

    public void Dispose()
    {
        ReplJsonBridge.Writer = _originalWriter;
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        foreach (var c in _contexts) { c.Emitter.Dispose(); c.Factory.Dispose(); }
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    // Streams the given updates as one model call, running `during` partway through, the way the
    // function-invocation loop would surface a call and its result.
    private sealed class ScriptedChatClient(IReadOnlyList<ChatResponseUpdate> updates, Action? during = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < updates.Count; i++)
            {
                if (i == updates.Count / 2) during?.Invoke();
                await Task.Yield();
                yield return updates[i];
            }
        }

        public object? GetService(Type serviceType, object? key = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private static ChatResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, [.. contents]);

    private ReplSessionContext NewContext(IChatClient client, ReplStdinPump? pump = null)
    {
        var eventsPath = Path.Combine(_home, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: _home, sessionId: "bridge-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath), eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_home, $"mem-{Guid.NewGuid():N}")),
            toolsByCategory: new(StringComparer.OrdinalIgnoreCase), systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new())
        {
            StdinPump = pump,
        };
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    private List<JsonElement> Events(string type) =>
        [.. _events.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("type").GetString() == type)];

    private static Task RunTurnAsync(ReplSessionContext ctx, string input = "go") =>
        ReplTurn.ExecuteAsync(ctx, input, isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

    [Fact]
    public async Task Turn_EmitsThinking_ToolCall_ToolResult_AndFileChange()
    {
        var args = new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" };
        var ctx = NewContext(new ScriptedChatClient(
        [
            Update(new TextReasoningContent("weighing it up")),
            Update(new FunctionCallContent("call-1", "write_file", args)),
            Update(new FunctionResultContent("call-1", "Wrote notes.txt")),
            Update(new TextContent("Done.")),
        ]));
        ctx.FileWriteStats[Path.GetFullPath(Path.Combine(_home, "notes.txt"))] = (3, 1);

        await RunTurnAsync(ctx);

        Assert.Equal("weighing it up", Assert.Single(Events("thinking")).GetProperty("text").GetString());

        var call = Assert.Single(Events("tool_call"));
        Assert.Equal("call-1", call.GetProperty("id").GetString());
        Assert.Equal("write_file", call.GetProperty("name").GetString());
        Assert.Contains("notes.txt", call.GetProperty("summary").GetString());

        var result = Assert.Single(Events("tool_result"));
        Assert.Equal("call-1", result.GetProperty("id").GetString());
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("Wrote notes.txt", result.GetProperty("output").GetString());

        var change = Assert.Single(Events("file_changed"));
        Assert.Equal("notes.txt", change.GetProperty("path").GetString());
        Assert.Equal("A", change.GetProperty("sigil").GetString());
        Assert.Equal(3, change.GetProperty("added").GetInt32());
        Assert.Equal(1, change.GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task FailedTool_IsNotOk_AndReportsNoFileChange()
    {
        var args = new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" };
        var ctx = NewContext(new ScriptedChatClient(
        [
            Update(new FunctionCallContent("call-1", "write_file", args)),
            Update(new FunctionResultContent("call-1", "[DENIED] Action blocked by user.")),
            Update(new TextContent("Okay, I won't.")),
        ]));

        await RunTurnAsync(ctx);

        Assert.False(Assert.Single(Events("tool_result")).GetProperty("ok").GetBoolean());
        Assert.Empty(Events("file_changed"));
    }

    [Fact]
    public async Task HugeToolOutput_IsCapped()
    {
        var ctx = NewContext(new ScriptedChatClient(
        [
            Update(new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "big.log" })),
            Update(new FunctionResultContent("call-1", new string('x', ReplJsonBridge.MaxToolOutputChars + 500))),
            Update(new TextContent("That's a big file.")),
        ]));

        await RunTurnAsync(ctx);

        var output = Assert.Single(Events("tool_result")).GetProperty("output").GetString()!;
        Assert.True(output.Length < ReplJsonBridge.MaxToolOutputChars + 100);
        Assert.Contains("500 more characters", output);
    }

    [Fact]
    public async Task MessageSentMidTurn_JoinsThroughThePump_AndCarriesWhenTheTurnDoesNotReadIt()
    {
        var pump = new ReplStdinPump(new StringReader(""), () => null);
        bool? taken = null;
        // One model call and no tool results, so the turn never reaches a point where it could read the message.
        var client = new ReplInterjectingChatClient(new ScriptedChatClient(
            [Update(new TextContent("Working")), Update(new TextContent(" on it."))],
            during: () => taken = pump.Interject?.Invoke("also add tests")));
        var ctx = NewContext(client, pump);

        await RunTurnAsync(ctx);

        Assert.True(taken);
        Assert.Null(pump.Interject);
        Assert.Equal("also add tests", ctx.CarriedInput);
        var carried = Assert.Single(Events("interjection"));
        Assert.Equal("carried", carried.GetProperty("status").GetString());
        Assert.Equal(1, carried.GetProperty("count").GetInt32());
    }

    [Fact]
    public void StatusEvent_CarriesTheStatusLine()
    {
        var json = JsonSerializer.SerializeToElement(
            ReplTurn.StatusEvent(new ReplStatus("grok-4.5", 3, 0.45678, ContextLevel.Near, ["hitl", "safe"])),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Equal("status", json.GetProperty("type").GetString());
        Assert.Equal("grok-4.5", json.GetProperty("model").GetString());
        Assert.Equal(3, json.GetProperty("turn").GetInt32());
        Assert.Equal(0.4568, json.GetProperty("context").GetDouble());
        Assert.Equal("near", json.GetProperty("level").GetString());
        Assert.Equal(["hitl", "safe"], json.GetProperty("modes").EnumerateArray().Select(m => m.GetString()));
    }
}
