using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>The wall-clock limits (explore 8 / locate 2 / delegate 15 minutes by default) come from config.</summary>
public sealed class SubagentPluginTimeoutTests
{
    // A fraction of a minute, so the test waits well under a second.
    private const double TinyMinutes = 0.004;

    private static readonly AIFunction FakeTool =
        AIFunctionFactory.Create((string path) => "ok", "fake_read", "Reads a file.");

    private sealed class HangingClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ChatResponse();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    // Backstop: if the configured limit were ignored these calls would sit on the 8/2/15-minute defaults
    // and hang the suite. Cancelling instead returns "was cancelled", which fails the assertion quickly.
    private static CancellationToken Backstop() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static SubagentPlugin Plugin(double explore = 0, double locate = 0, double delegateMinutes = 0) =>
        new(new HangingClient(), explorerTools: [FakeTool], delegateTools: [FakeTool],
            exploreTimeoutMinutes: explore, locateTimeoutMinutes: locate, delegateTimeoutMinutes: delegateMinutes);

    [Fact]
    public async Task Explore_HonoursItsConfiguredTimeout()
    {
        var result = await Plugin(explore: TinyMinutes).ExploreAsync("q", cancellationToken: Backstop());

        Assert.Contains($"timed out after {TinyMinutes} minutes", result);
    }

    [Fact]
    public async Task Locate_HonoursItsConfiguredTimeout()
    {
        var result = await Plugin(locate: TinyMinutes).LocateAsync("Foo", Backstop());

        Assert.Contains($"timed out after {TinyMinutes} minutes", result);
    }

    [Fact]
    public async Task Delegate_HonoursItsConfiguredTimeout()
    {
        var result = await Plugin(delegateMinutes: TinyMinutes).DelegateAsync("do it", Backstop());

        Assert.Contains($"timed out after {TinyMinutes} minutes", result);
    }

    [Fact]
    public async Task Streaming_HonoursTheConfiguredTimeoutToo()
    {
        var (result, _, _) = await Plugin(explore: TinyMinutes)
            .ExploreStreamingAsync("q", _ => Task.CompletedTask, cancellationToken: Backstop());

        Assert.Contains($"timed out after {TinyMinutes} minutes", result);
    }

    [Fact]
    public async Task OneLimit_DoesNotShortenTheOthers()
    {
        // Explore is set to time out almost instantly; Delegate keeps its own (long) default and is
        // cancelled by the token here instead — so it must NOT report the explore limit.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var result = await Plugin(explore: TinyMinutes).DelegateAsync("do it", cts.Token);

        Assert.DoesNotContain("timed out", result);
    }
}
