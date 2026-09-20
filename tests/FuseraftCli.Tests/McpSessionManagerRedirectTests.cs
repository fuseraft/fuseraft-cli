using fuseraft.Core.Models;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

/// <summary>
/// Configured MCP auth headers (<c>X-Api-Key</c>, <c>Authorization</c>, …) belong to the server the
/// user configured — they must not follow an HTTP redirect to a different origin. .NET's redirect
/// handling only strips <c>Authorization</c>, so a custom header like <c>X-Api-Key</c> would
/// otherwise be forwarded verbatim to wherever the server (or an open redirect in front of it)
/// points. Exercised end to end through <see cref="McpSessionManager.ConnectSingleAsync"/> against
/// real loopback listeners; none of them speaks MCP, so the connect itself is expected to fail —
/// what matters is what the *other* origin was sent.
/// </summary>
[Collection("LoopbackPorts")]
public sealed class McpSessionManagerRedirectTests
{
    private static async Task TryConnectAsync(McpServerConfig config)
    {
        await using var manager = new McpSessionManager();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await manager.ConnectSingleAsync(config, cts.Token); }
        catch { /* neither listener is an MCP server — the connect is expected to fail */ }
    }

    [Fact]
    public async Task ConfiguredHeader_IsNotForwardedToADifferentOrigin_OnRedirect()
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 400));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/collect"));

        await TryConnectAsync(new McpServerConfig
        {
            Name      = "redirecting",
            Transport = "http",
            Url       = origin.BaseUrl + "/mcp",
            Headers   = new() { ["X-Api-Key"] = "super-secret-key-123" },
        });

        Assert.True(origin.SawHeader("X-Api-Key", "super-secret-key-123"),
            "sanity check: the configured origin should have received its own header");
        Assert.NotEmpty(other.Requests);                           // the redirect really was followed
        Assert.False(other.SawHeader("X-Api-Key"),
            "a configured auth header leaked to a different origin via redirect");
    }
}
