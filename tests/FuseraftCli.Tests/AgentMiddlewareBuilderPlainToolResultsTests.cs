using System.Text.Json;
using fuseraft.Infrastructure.Agents;
using fuseraft.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

/// <summary>
/// A tool's text result reaches the provider as text: sent as a JSON string element, it was serialized to an
/// escaped, quoted string ("import os\n…"), 12–14% more tokens on xAI.
/// </summary>
public sealed class AgentMiddlewareBuilderPlainToolResultsTests
{
    private static ChatMessage Result(object? value) =>
        new(ChatRole.Tool, [new FunctionResultContent("c1", value) { Exception = null }]) { AuthorName = "repl" };

    [Fact]
    public void AJsonStringResult_IsSentAsItsText()
    {
        var original = Result(JsonSerializer.SerializeToElement("import os\nprint(1)\n"));

        var sent = AgentMiddlewareBuilder.WithPlainTextToolResults([new(ChatRole.User, "go"), original]).ToList();

        var result = sent[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("import os\nprint(1)\n", Assert.IsType<string>(result.Result));
        Assert.Equal("c1", result.CallId);
        Assert.Equal("repl", sent[1].AuthorName);
        // The history itself is left as it was.
        Assert.IsType<JsonElement>(original.Contents.OfType<FunctionResultContent>().Single().Result);
    }

    [Fact]
    public void AStructuredResult_StaysJson()
    {
        var json = JsonSerializer.SerializeToElement(new { exitCode = 0 });

        var sent = AgentMiddlewareBuilder.WithPlainTextToolResults([Result(json)]).Single();

        Assert.IsType<JsonElement>(sent.Contents.OfType<FunctionResultContent>().Single().Result);
    }

    [Fact]
    public void WithNothingToChange_TheSameMessagesAreSent()
    {
        List<ChatMessage> messages = [new(ChatRole.User, "go"), Result("plain")];

        Assert.Same(messages, AgentMiddlewareBuilder.WithPlainTextToolResults(messages));
    }

    [Fact]
    public async Task TheProvider_GetsTheText()
    {
        var inner = new CapturingClient();
        var client = new AgentMiddlewareBuilder(NullLogger.Instance, changeTracker: null, securityConfig: null, governanceKernel: null)
            .BuildMiddlewareChain(
                chatClient: inner, config: new AgentConfig { Name = "a", Model = new() { ModelId = "m" } }, chatOptions: null,
                maxContextChars: 0, maxInTurnChars: 0, maxInTurnToolPairs: 0, toolSchemaChars: 0, maxPayloadBytes: 0,
                hasHandoff: false);
        List<ChatMessage> history = [new(ChatRole.User, "go"), Result(JsonSerializer.SerializeToElement("line 1\n"))];

        await client.GetResponseAsync(history);
        await foreach (var _ in client.GetStreamingResponseAsync(history)) { }

        Assert.All(inner.Sent, sent =>
            Assert.Equal("line 1\n", Assert.IsType<string>(sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result)));
    }

    private sealed class CapturingClient : IChatClient
    {
        public List<List<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
