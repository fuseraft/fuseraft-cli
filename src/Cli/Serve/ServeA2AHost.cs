using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Models.Agents;

namespace fuseraft.Cli.Serve;

/// <summary>How <c>fuseraft serve --a2a</c> listens; <see cref="Token"/> comes from <see cref="ServeA2AHost.TokenVariable"/>.</summary>
public sealed record ServeA2AOptions(string Bind, int Port, string? Token, IReadOnlyList<string> AgentNames);

/// <summary>
/// Hosts a <c>fuseraft serve</c> daemon's agents over A2A, one endpoint per agent at
/// <c>/{agent}</c>, so a fuseraft team on another machine can add them as <c>RemoteAgent</c>
/// members. Each call runs just that agent — its own instructions and tools, under the daemon's
/// approval policy — not the team's routing or validators.
/// </summary>
public static class ServeA2AHost
{
    public const string TokenVariable = "FUSERAFT_A2A_TOKEN";

    /// <summary>The agents to host: every local agent, or the named ones. Throws on a name the config doesn't have, or one that is itself remote.</summary>
    public static IReadOnlyList<AgentConfig> SelectAgents(IReadOnlyList<AgentConfig> agents, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return [.. agents.Where(a => a.RemoteAgent is null)];

        return [.. names.Select(name =>
        {
            var agent = agents.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"--a2a-agent '{name}' is not in the config. Agents: {string.Join(", ", agents.Select(a => a.Name))}.");
            return agent.RemoteAgent is null
                ? agent
                : throw new InvalidOperationException($"--a2a-agent '{name}' is itself a RemoteAgent; only local agents can be hosted.");
        })];
    }

    /// <summary>Null when the options are usable, otherwise why not.</summary>
    public static string? Validate(ServeA2AOptions options) =>
        options.Port is < 1 or > 65535
            ? $"--a2a-port must be between 1 and 65535, got {options.Port}."
            : !IsLoopback(options.Bind) && string.IsNullOrEmpty(options.Token)
                ? $"--a2a-bind {options.Bind} accepts connections from other machines, so callers must authenticate. " +
                  $"Set {TokenVariable} to a secret and give callers RemoteAgent.Headers: {{ Authorization: \"Bearer <token>\" }}."
                : null;

    public static bool IsLoopback(string bind) =>
        bind.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(bind.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <param name="agents">The built agents, each named after its config.</param>
    /// <param name="runGate">Taken around every call; the daemon's tasks share the same agents, working directory and session-scoped state.</param>
    public static WebApplication Build(
        ServeA2AOptions options,
        IReadOnlyList<(AgentConfig Config, AIAgent Agent)> agents,
        Func<string, CancellationToken, Task<IAsyncDisposable>> runGate)
    {
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseUrls($"http://{UrlHost(options.Bind)}:{options.Port}");
        builder.Logging.ClearProviders();

        foreach (var (config, agent) in agents)
        {
            // The session store is [Experimental] (MAAI001); without one the server forgets each
            // A2A context between calls, so a caller's multi-turn conversation starts over every time.
#pragma warning disable MAAI001
            builder.Services.AddKeyedSingleton<AgentSessionStore>(config.Name, new InMemoryAgentSessionStore());
#pragma warning restore MAAI001
            builder.AddA2AServer(new GatedAgent(agent, runGate));
        }

        var app = builder.Build();
        // A TLS proxy in front would otherwise get cards naming its plain-HTTP hop. Only proxies
        // on loopback are believed (the default), so a remote caller can't rewrite the URL.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        });
        if (!string.IsNullOrEmpty(options.Token))
            app.Use(RequireBearer(options.Token));

        foreach (var (config, _) in agents)
        {
            var path = "/" + Uri.EscapeDataString(config.Name);
            app.MapA2AJsonRpc(config.Name, path);
            // Built per request so the card names the address the caller actually reached —
            // a bind of 0.0.0.0 says nothing about which of the machine's names that was.
            app.MapGet(path + "/.well-known/agent-card.json", (HttpContext ctx) =>
                Results.Json(Card(config, $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{path}"),
                    A2A.A2AJsonUtilities.DefaultOptions));
        }
        return app;
    }

    /// <summary>The <c>RemoteAgent.Url</c> a caller on this machine would use for an agent.</summary>
    public static string AgentUrl(ServeA2AOptions options, string agentName, int boundPort) =>
        $"http://{(IsLoopback(options.Bind) || IsAnyAddress(options.Bind) ? "localhost" : UrlHost(options.Bind))}:{boundPort}/{Uri.EscapeDataString(agentName)}";

    internal static AgentCard Card(AgentConfig config, string url) => new()
    {
        Name               = config.Name,
        Description        = string.IsNullOrWhiteSpace(config.Description) ? $"fuseraft agent {config.Name}" : config.Description,
        Version            = typeof(ServeA2AHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
        DefaultInputModes  = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Capabilities       = new() { Streaming = false, PushNotifications = false },
        Skills             = [new() { Id = config.Name, Name = config.Name, Description = config.Description ?? "", Tags = ["fuseraft"] }],
        SupportedInterfaces = [new() { Url = url, ProtocolBinding = ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0" }],
    };

    private static Func<HttpContext, RequestDelegate, Task> RequireBearer(string token)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        return async (ctx, next) =>
        {
            var actual = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(ctx);
        };
    }

    private static bool IsAnyAddress(string bind) =>
        bind is "*" or "+" || (IPAddress.TryParse(bind.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));

    private static string UrlHost(string bind) =>
        IPAddress.TryParse(bind.Trim('[', ']'), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ip}]"
            : bind;

    // A call holds the gate for its whole run, so it never overlaps a dispatched task or another call.
    private sealed class GatedAgent(AIAgent inner, Func<string, CancellationToken, Task<IAsyncDisposable>> runGate)
        : DelegatingAIAgent(inner)
    {
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        {
            await using var _ = await runGate(Name!, cancellationToken);
            return await base.RunCoreAsync(messages, session, options, cancellationToken);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var _ = await runGate(Name!, cancellationToken);
            await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken))
                yield return update;
        }
    }
}
