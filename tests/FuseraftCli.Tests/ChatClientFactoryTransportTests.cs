using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary>
/// Ollama is built on its own HttpClient (OllamaSharp's, 100 s by default), so the shared client's
/// request timeout never reached it — a slow local model timed out with no way to raise the limit.
/// </summary>
public sealed class ChatClientFactoryTransportTests
{
    private static readonly ModelConfig Ollama = new()
    {
        ModelId = "llama3:latest", Provider = "ollama", Endpoint = "http://localhost:11434",
    };

    [Fact]
    public void Ollama_ConfiguredRequestTimeout_IsApplied()
    {
        using var factory = new ChatClientFactory(transport: TransportOptions.From(new UserConfig { RequestTimeoutSeconds = 900 }));

        using var _ = factory.Create(Ollama) as IDisposable;

        var client = Assert.Single(factory.OllamaHttpClients);
        Assert.Equal(TimeSpan.FromSeconds(900), client.Timeout);
        Assert.Equal(new Uri("http://localhost:11434"), client.BaseAddress);
    }

    [Fact]
    public void Ollama_UnsetRequestTimeout_KeepsItsExistingDefault()
    {
        using var factory = new ChatClientFactory();

        using var _ = factory.Create(Ollama) as IDisposable;

        Assert.Equal(TimeSpan.FromSeconds(100), Assert.Single(factory.OllamaHttpClients).Timeout);
    }

    [Fact]
    public void Dispose_ReleasesTheOllamaHttpClients()
    {
        var factory = new ChatClientFactory();
        factory.Create(Ollama);
        var client = Assert.Single(factory.OllamaHttpClients);

        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Timeout = TimeSpan.FromSeconds(5));
    }

    // A server that accepts every connection and hangs up — the failure the retry layers exist for.
    private static async Task<int> AttemptsMadeAsync(TransportOptions transport)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var accepted = 0;
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (Exception) { /* listener stopped */ }
        });

        using var factory = new ChatClientFactory(transport: transport);
        using var client = factory.Create(new ModelConfig
        {
            ModelId  = "gpt-4o-mini", Provider = "openai", ApiKey = "sk-test",
            Endpoint = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/v1",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")], cancellationToken: cts.Token));

        listener.Stop();
        await accepting;
        return accepted;
    }

    [Fact]
    public async Task ZeroRetriesConfigured_MeansExactlyOneAttempt_NotTheSdksOwnRetriesStackedOnTop()
    {
        var attempts = await AttemptsMadeAsync(TransportOptions.From(new UserConfig { MaxRetries = 0 }));

        Assert.Equal(1, attempts);
    }
}
