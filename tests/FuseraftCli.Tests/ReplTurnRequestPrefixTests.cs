using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// A provider's prompt cache serves a request only up to its first changed message. Across a REPL session's turns,
/// through the real client chain, each request must start with the exact bytes of the one before it: the history a
/// turn keeps has to match what its rounds sent.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnRequestPrefixTests : IDisposable
{
    private static string Chunk(string delta, string? finish = null) =>
        $$"""data: {"id":"x","object":"chat.completion.chunk","created":1,"model":"grok-4.5","choices":[{"index":0,"delta":{{delta}}{{(finish is null ? "" : $",\"finish_reason\":\"{finish}\"")}}}]}""" + "\n\n";

    private static string Stream(params string[] chunks) =>
        string.Concat(chunks) +
        """data: {"id":"x","object":"chat.completion.chunk","created":1,"model":"grok-4.5","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":1,"total_tokens":11}}""" + "\n\ndata: [DONE]\n\n";

    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    [Fact]
    public async Task AcrossTurns_EachRequestExtendsTheOneBefore()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);
        string[] streams =
        [
            // turn 1: reasoning + text + two parallel calls, then the answer
            Stream(
                Chunk("""{"role":"assistant","reasoning_content":"I should "}"""),
                Chunk("""{"reasoning_content":"check."}"""),
                Chunk("""{"content":"Let me "}"""),
                Chunk("""{"content":"check."}"""),
                Chunk("""{"tool_calls":[{"id":"call-0","index":0,"type":"function","function":{"name":"get_time","arguments":""}}]}"""),
                Chunk("""{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}"""),
                Chunk("""{"tool_calls":[{"id":"call-1","index":1,"type":"function","function":{"name":"get_date","arguments":"{}"}}]}"""),
                Chunk("{}", "tool_calls")),
            Stream(Chunk("""{"role":"assistant","content":"It is "}"""), Chunk("""{"content":"12:00."}"""), Chunk("{}", "stop")),
            // turn 2: one call, then the answer
            Stream(Chunk("""{"role":"assistant","tool_calls":[{"id":"call-2","index":0,"type":"function","function":{"name":"get_time","arguments":"{}"}}]}"""), Chunk("{}", "tool_calls")),
            Stream(Chunk("""{"role":"assistant","content":"Still 12:00."}"""), Chunk("{}", "stop")),
            // turn 3
            Stream(Chunk("""{"role":"assistant","content":"Bye."}"""), Chunk("{}", "stop")),
        ];
        var next = 0;
        await using var server = new LoopbackHttpServer((_, res) =>
        {
            var body = Encoding.UTF8.GetBytes(streams[Math.Min(next++, streams.Length - 1)]);
            res.ContentType = "text/event-stream";
            return res.OutputStream.WriteAsync(body, 0, body.Length);
        });

        var eventsPath = Path.Combine(_tempHome, "events.jsonl");
        Directory.CreateDirectory(_tempHome);
        AIFunction[] tools =
        [
            AIFunctionFactory.Create(() => "12:00", "get_time", "Get the time"),
            AIFunctionFactory.Create(() => "{\"date\":\"2026-11-26\"}", "get_date", "Get the date"),
        ];
        var model   = new ModelConfig { ModelId = "grok-4.5", Provider = "openai", ApiKey = "test", Endpoint = server.BaseUrl + "/v1" };
        var factory = new ChatClientFactory();
        var tracker = new AdaptiveTrimTracker();
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "probe", startedAt: DateTime.UtcNow,
            modelId: model.ModelId, modelConfig: model, userCfg: null,
            client: ReplFactory.BuildClient(model, factory, addFunctionInvocation: true, tracker, tools: tools),
            factory: factory, keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath), eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, "mem")),
            toolsByCategory: new() { ["Test"] = [.. tools] }, systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: tracker);
        ctx.JsonMode = true;

        foreach (var input in new[] { "What time is it?", "And now?", "Thanks" })
            await ReplTurn.ExecuteAsync(ctx, input, isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        ctx.Emitter.Dispose(); factory.Dispose();

        var requests = server.Requests.Select(r => JsonNode.Parse(r.Body)!).ToList();
        Assert.Equal(5, requests.Count);
        for (var i = 1; i < requests.Count; i++)
        {
            var prev = requests[i - 1]["messages"]!.AsArray().Select(m => m!.ToJsonString()).ToList();
            var cur  = requests[i]["messages"]!.AsArray().Select(m => m!.ToJsonString()).ToList();
            Assert.True(cur.Count > prev.Count, $"request {i} is shorter than the one before");
            Assert.Equal(prev, cur.Take(prev.Count));
            Assert.Equal(requests[i - 1]["tools"]!.ToJsonString(), requests[i]["tools"]!.ToJsonString());
        }
    }
}
