using System.Text;
using System.Text.Json;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;

namespace FuseraftCli.Tests;

/// <summary><c>models --provider/--endpoint</c>: a host lists an unsaved connection's models with FUSERAFT_API_KEY.</summary>
[Collection("FuseraftHomeEnv")] // swaps WireProtocol.Writer and FUSERAFT_API_KEY
public sealed class ModelsCommandTests : IDisposable
{
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly string? _originalKey = Environment.GetEnvironmentVariable("FUSERAFT_API_KEY");
    private readonly StringWriter _out = new();

    public ModelsCommandTests() => WireProtocol.Writer = _out;

    public void Dispose()
    {
        WireProtocol.Writer = _originalWriter;
        Environment.SetEnvironmentVariable("FUSERAFT_API_KEY", _originalKey);
    }

    private static Task<int> RunAsync(ModelsSettings settings) =>
        new ModelsCommand().ExecuteAsync(null!, settings, CancellationToken.None);

    private static LoopbackHttpServer Serve(string json) => new(async (_, res) =>
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json";
        await res.OutputStream.WriteAsync(bytes);
    });

    [Fact]
    public async Task Endpoint_ListsItsModels_WithTheHostsKey()
    {
        await using var server = Serve("""{"data":[{"id":"b-model"},{"id":"a-model"}]}""");
        Environment.SetEnvironmentVariable("FUSERAFT_API_KEY", "sk-host");

        Assert.Equal(0, await RunAsync(new ModelsSettings { Json = true, Endpoint = server.BaseUrl + "/v1/" }));

        using var doc = JsonDocument.Parse(_out.ToString());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["a-model", "b-model"], items.Select(i => i.GetProperty("id").GetString()));
        Assert.All(items, i => Assert.False(i.GetProperty("current").GetBoolean()));
        var request = Assert.Single(server.Requests);
        Assert.Equal("/v1/models", request.Path);
        Assert.Equal("Bearer sk-host", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task Ollama_ReadsItsTags_WithoutAKey()
    {
        await using var server = Serve("""{"models":[{"name":"qwen3:8b"}]}""");
        Environment.SetEnvironmentVariable("FUSERAFT_API_KEY", null);

        Assert.Equal(0, await RunAsync(new ModelsSettings { Json = true, Provider = "ollama", Endpoint = server.BaseUrl }));

        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Equal("qwen3:8b", doc.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        var request = Assert.Single(server.Requests);
        Assert.Equal("/api/tags", request.Path);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ProviderWithoutADefaultEndpoint_IsAnErrorEnvelope()
    {
        Assert.Equal(1, await RunAsync(new ModelsSettings { Json = true, Provider = "azure" }));

        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Contains("Pass --endpoint", doc.RootElement.GetProperty("error").GetString());
    }
}
