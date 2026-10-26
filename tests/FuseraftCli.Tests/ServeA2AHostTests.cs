using System.Net;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Serve;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ServeA2AHost"/> — which agents are hosted, when a token is required, and a real
/// A2A round trip through fuseraft's own RemoteAgent client.
/// </summary>
public sealed class ServeA2AHostTests
{
    private const string Token = "s3cret-token";

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("myhost.lan", false)]
    public void IsLoopback(string bind, bool expected) =>
        Assert.Equal(expected, ServeA2AHost.IsLoopback(bind));

    [Fact]
    public void Validate_BeyondLoopbackWithoutAToken_IsRefused()
    {
        var problem = ServeA2AHost.Validate(new ServeA2AOptions("0.0.0.0", 5000, null, []));

        Assert.NotNull(problem);
        Assert.Contains(ServeA2AHost.TokenVariable, problem);
    }

    [Theory]
    [InlineData("0.0.0.0", Token)]
    [InlineData("localhost", null)]
    public void Validate_AcceptsATokenOrLoopback(string bind, string? token) =>
        Assert.Null(ServeA2AHost.Validate(new ServeA2AOptions(bind, 5000, token, [])));

    [Fact]
    public void Validate_RejectsAnOutOfRangePort() =>
        Assert.Contains("--a2a-port", ServeA2AHost.Validate(new ServeA2AOptions("localhost", 70000, null, [])));

    [Fact]
    public void SelectAgents_DefaultsToEveryLocalAgent()
    {
        var selected = ServeA2AHost.SelectAgents([Agent("Dev"), Remote("Elsewhere"), Agent("Tester")], []);

        Assert.Equal(["Dev", "Tester"], selected.Select(a => a.Name));
    }

    [Fact]
    public void SelectAgents_NamedAgentsAreMatchedIgnoringCase()
    {
        var selected = ServeA2AHost.SelectAgents([Agent("Dev"), Agent("Tester")], ["tester"]);

        Assert.Equal("Tester", Assert.Single(selected).Name);
    }

    [Fact]
    public void SelectAgents_UnknownName_ListsTheConfigsAgents()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServeA2AHost.SelectAgents([Agent("Dev")], ["Nobody"]));

        Assert.Contains("'Nobody' is not in the config", ex.Message);
        Assert.Contains("Dev", ex.Message);
    }

    [Fact]
    public void SelectAgents_ARemoteAgentCannotBeHosted() =>
        Assert.Contains("itself a RemoteAgent",
            Assert.Throws<InvalidOperationException>(() => ServeA2AHost.SelectAgents([Remote("Elsewhere")], ["Elsewhere"])).Message);

    [Theory]
    [InlineData("0.0.0.0", "http://localhost:5001/Code%20Reviewer")]
    [InlineData("localhost", "http://localhost:5001/Code%20Reviewer")]
    [InlineData("192.168.1.20", "http://192.168.1.20:5001/Code%20Reviewer")]
    public void AgentUrl_EscapesTheName(string bind, string expected) =>
        Assert.Equal(expected, ServeA2AHost.AgentUrl(new ServeA2AOptions(bind, 5001, Token, []), "Code Reviewer", 5001));

    [Fact]
    public async Task Build_WithAToken_RejectsACallerWithoutIt()
    {
        await using var app = await StartAsync(new ReplyingChatClient("pong"), (_, _) => Task.FromResult<IAsyncDisposable>(new Noop()));
        using var http = new HttpClient();

        var response = await http.GetAsync(BaseUrl(app) + "/Helper/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Build_CardNamesTheAddressALocalTlsProxyForwarded()
    {
        await using var app = await StartAsync(new ReplyingChatClient("pong"), (_, _) => Task.FromResult<IAsyncDisposable>(new Noop()));
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl(app) + "/Helper/.well-known/agent-card.json");
        request.Headers.Add("Authorization", "Bearer " + Token);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "agents.example.com");

        var body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("\"https://agents.example.com/Helper\"", body);
    }

    [Fact]
    public async Task Build_RemoteAgentClient_RunsTheHostedAgentInsideTheRunGate()
    {
        var gate = new RecordingGate();
        var chat = new ReplyingChatClient("pong") { WhileRunning = () => gate.Held };
        await using var app = await StartAsync(chat, gate.EnterAsync);
        var remote = new RemoteAgentConfig
        {
            Url     = BaseUrl(app) + "/Helper",
            Headers = new() { ["Authorization"] = "Bearer " + Token },
        };
        using var http = RemoteAgentHttpClient.Create(remote);
        var resolver = new A2ACardResolver(RemoteAgentHttpClient.CardBaseUri(remote.Url), http);

        var card = await resolver.GetAgentCardAsync();
        var agent = await resolver.GetAIAgentAsync(new Microsoft.Agents.AI.A2A.A2AAgentOptions { Name = "Helper" }, http);
        var response = await agent.RunAsync("ping");

        Assert.Equal("Helper", card.Name);
        Assert.Equal(remote.Url, Assert.Single(card.SupportedInterfaces!).Url);
        Assert.Equal("pong", response.Text);
        Assert.True(chat.SawGateHeld);
        Assert.Equal(["Helper"], gate.Entered);
        Assert.False(gate.Held);
    }

    private static async Task<Microsoft.AspNetCore.Builder.WebApplication> StartAsync(
        IChatClient chat, Func<string, CancellationToken, Task<IAsyncDisposable>> runGate)
    {
        var config = Agent("Helper");
        AIAgent agent = new ChatClientAgent(chat, instructions: "help", name: config.Name);
        var app = ServeA2AHost.Build(new ServeA2AOptions("127.0.0.1", 0, Token, []), [(config, agent)], runGate);
        await app.StartAsync();
        return app;
    }

    private static string BaseUrl(Microsoft.AspNetCore.Builder.WebApplication app) => app.Urls.First().TrimEnd('/');

    private static AgentConfig Agent(string name) => new() { Name = name, Instructions = "x", Model = new ModelConfig { ModelId = "m" } };

    private static AgentConfig Remote(string name) => Agent(name) with { RemoteAgent = new RemoteAgentConfig { Url = "http://elsewhere:1" } };

    private sealed class RecordingGate
    {
        private int _held;
        public List<string> Entered { get; } = [];
        public bool Held => Volatile.Read(ref _held) > 0;

        public Task<IAsyncDisposable> EnterAsync(string agent, CancellationToken _)
        {
            lock (Entered) Entered.Add(agent);
            Interlocked.Increment(ref _held);
            return Task.FromResult<IAsyncDisposable>(new Release(() => Interlocked.Decrement(ref _held)));
        }
    }

    private sealed class Release(Action onRelease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { onRelease(); return ValueTask.CompletedTask; }
    }

    private sealed class Noop : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReplyingChatClient(string reply) : IChatClient
    {
        public Func<bool>? WhileRunning { get; init; }
        public bool SawGateHeld { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SawGateHeld = WhileRunning?.Invoke() ?? false;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
