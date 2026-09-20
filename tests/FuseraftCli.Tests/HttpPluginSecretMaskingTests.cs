using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// A local debug endpoint (<c>/actuator/env</c>, <c>/debug/vars</c>) or an echo service can hand a live
/// credential straight back in a response body, and the Http plugin returned bodies verbatim while
/// every process-output tool masked them. Two loopback listeners, real plugin, real shared client.
/// </summary>
[Collection("LoopbackPorts")]
public sealed class HttpPluginSecretMaskingTests : IDisposable
{
    private const string Secret = "http-env-secret-13572468";
    private readonly string _envName = $"FUSERAFT_HTTPMASK_{Guid.NewGuid():N}_API_KEY".ToUpperInvariant();

    public HttpPluginSecretMaskingTests() => Environment.SetEnvironmentVariable(_envName, Secret);

    public void Dispose() => Environment.SetEnvironmentVariable(_envName, null);

    private static HttpPlugin Http()
    {
        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { AllowPrivateHosts = true });
        Assert.True(registry.TryGet("Http", out var plugin));
        return Assert.IsType<HttpPlugin>(plugin);
    }

    [Fact]
    public async Task ASecretEchoedInTheBody_IsMasked()
    {
        await using var server = new LoopbackHttpServer((_, res) =>
            LoopbackHttpServer.Respond(res, 200, $"{{\"env\":{{\"API_KEY\":\"{Secret}\"}},\"port\":8080}}"));

        var result = await Http().GetAsync(server.BaseUrl + "/env");

        Assert.DoesNotContain(Secret, result);
        Assert.Contains(EnvSecretMasker.Placeholder, result);
        Assert.Contains("\"port\":8080", result);
    }

    [Fact]
    public async Task ASecretEchoedInAnErrorBody_IsMasked()
    {
        await using var server = new LoopbackHttpServer((_, res) =>
            LoopbackHttpServer.Respond(res, 500, $"boom: bad credential {Secret}"));

        var result = await Http().GetAsync(server.BaseUrl + "/x");

        Assert.Contains("[HTTP 500", result);
        Assert.DoesNotContain(Secret, result);
    }

    [Fact]
    public async Task AnOrdinaryBody_IsReturnedUntouched()
    {
        await using var server = new LoopbackHttpServer((_, res) =>
            LoopbackHttpServer.Respond(res, 200, "{\"hello\":\"world\"}"));

        var result = await Http().GetAsync(server.BaseUrl + "/x");

        Assert.Equal("{\"hello\":\"world\"}", result);
    }
}
