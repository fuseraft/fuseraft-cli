using System.ClientModel;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

// ---------------------------------------------------------------------------
// ProviderErrorClassifier tests
// ---------------------------------------------------------------------------

public sealed class ProviderErrorClassifierTests
{
    // ClientResultException helpers — the SDK constructor is internal so we
    // build real exceptions via the pipeline factory exposed for testing.
    // We fall back to wrapping in InvalidOperationException with a known message
    // for cases where status code is surfaced through the message string.

    [Theory]
    [InlineData("context_length_exceeded",  FailoverReason.ContextExceeded)]
    [InlineData("too many tokens",          FailoverReason.ContextExceeded)]
    [InlineData("prompt is too long",       FailoverReason.ContextExceeded)]
    [InlineData("reduce the length of the", FailoverReason.ContextExceeded)]
    [InlineData("context window is full",   FailoverReason.ContextExceeded)]
    [InlineData("token limit exceeded",     FailoverReason.ContextExceeded)]
    public void Classify_ReturnsContextExceeded_ForContextMessages(string snippet, FailoverReason expected)
    {
        var ex = new InvalidOperationException($"API error: {snippet}");
        Assert.Equal(expected, ProviderErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_ReturnsRateLimit_For429MessageWithoutQuotaWords()
    {
        var ex = new InvalidOperationException("HTTP 429 Too Many Requests from api.openai.com");
        Assert.Equal(FailoverReason.RateLimit, ProviderErrorClassifier.Classify(ex));
    }

    [Theory]
    [InlineData("You have exceeded your monthly quota")]
    [InlineData("billing limit reached")]
    [InlineData("used all available credits")]
    [InlineData("spending limit exceeded")]
    public void Classify_ReturnsQuotaExceeded_For429WithQuotaWords(string quotaSnippet)
    {
        var ex = new InvalidOperationException($"429: {quotaSnippet}");
        Assert.Equal(FailoverReason.QuotaExceeded, ProviderErrorClassifier.Classify(ex));
    }

    [Theory]
    [InlineData("Unauthorized — check your API key")]
    [InlineData("Forbidden: access denied")]
    [InlineData("Invalid API key provided")]
    [InlineData("error code: invalid_api_key")]
    public void Classify_ReturnsAuthError_ForAuthMessages(string snippet)
    {
        var ex = new InvalidOperationException(snippet);
        Assert.Equal(FailoverReason.AuthError, ProviderErrorClassifier.Classify(ex));
    }

    [Theory]
    [InlineData("Internal Server Error")]
    [InlineData("Bad Gateway")]
    [InlineData("Service Unavailable")]
    [InlineData("Gateway Timeout")]
    public void Classify_ReturnsServerError_ForServerErrorMessages(string snippet)
    {
        var ex = new InvalidOperationException(snippet);
        Assert.Equal(FailoverReason.ServerError, ProviderErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_ReturnsNone_ForUnrecognizedMessage()
    {
        var ex = new InvalidOperationException("Something went completely wrong");
        Assert.Equal(FailoverReason.None, ProviderErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_WalksInnerExceptions()
    {
        var inner = new InvalidOperationException("context_length_exceeded");
        var outer = new Exception("Outer wrapper", inner);
        Assert.Equal(FailoverReason.ContextExceeded, ProviderErrorClassifier.Classify(outer));
    }

    [Fact]
    public void Classify_PrefersContextExceededOverRateLimit_WhenBothPresent()
    {
        // A message that contains "429" but also "context_length_exceeded"
        var ex = new InvalidOperationException("429 context_length_exceeded — prompt is too long");
        Assert.Equal(FailoverReason.ContextExceeded, ProviderErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_UsesHttpRequestExceptionStatusCode()
    {
        var ex = new HttpRequestException("server error", null, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(FailoverReason.ServerError, ProviderErrorClassifier.Classify(ex));
    }

    // ParseFalloverOn

    [Fact]
    public void ParseFalloverOn_ReturnsDefault_WhenNull()
    {
        var result = ProviderErrorClassifier.ParseFalloverOn(null);
        Assert.Equal(ProviderErrorClassifier.DefaultFalloverOn, result);
    }

    [Fact]
    public void ParseFalloverOn_ParsesKnownValues()
    {
        var result = ProviderErrorClassifier.ParseFalloverOn(["RateLimit", "ContextExceeded"]);
        Assert.Contains(FailoverReason.RateLimit, result);
        Assert.Contains(FailoverReason.ContextExceeded, result);
        Assert.DoesNotContain(FailoverReason.ServerError, result);
    }

    [Fact]
    public void ParseFalloverOn_IsCaseInsensitive()
    {
        var result = ProviderErrorClassifier.ParseFalloverOn(["ratelimit", "SERVERROR", "serverError"]);
        Assert.Contains(FailoverReason.RateLimit, result);
        Assert.Contains(FailoverReason.ServerError, result);
    }

    [Fact]
    public void ParseFalloverOn_IgnoresUnrecognizedValues()
    {
        var result = ProviderErrorClassifier.ParseFalloverOn(["RateLimit", "UnknownReason"]);
        Assert.Contains(FailoverReason.RateLimit, result);
        Assert.Single(result);
    }
}

// ---------------------------------------------------------------------------
// FalloverChatClient tests
// ---------------------------------------------------------------------------

public sealed class FalloverChatClientTests
{
    private static readonly IReadOnlySet<FailoverReason> AllReasons =
        ProviderErrorClassifier.DefaultFalloverOn;

    // GetResponseAsync — success on first slot

    [Fact]
    public async Task GetResponseAsync_ReturnsFirstSlot_WhenPrimarySucceeds()
    {
        var primary  = new FakeClient(() => Task.FromResult(FakeResponse("primary")));
        var fallover = new FakeClient(() => Task.FromResult(FakeResponse("fallover")));

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        var result = await sut.GetResponseAsync([]);

        Assert.Equal("primary", result.Text);
        Assert.Equal(0, fallover.CallCount);
    }

    // GetResponseAsync — fallover on classifiable error

    [Fact]
    public async Task GetResponseAsync_FallsOver_WhenPrimaryThrowsRateLimit()
    {
        var primary  = new FakeClient(() => throw new InvalidOperationException("HTTP 429 Too Many Requests"));
        var fallover = new FakeClient(() => Task.FromResult(FakeResponse("fallover")));

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        var result = await sut.GetResponseAsync([]);

        Assert.Equal("fallover", result.Text);
        Assert.Equal(1, fallover.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_FallsOver_WhenPrimaryThrowsContextExceeded()
    {
        var primary  = new FakeClient(() => throw new InvalidOperationException("context_length_exceeded"));
        var fallover = new FakeClient(() => Task.FromResult(FakeResponse("fallover")));

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        var result = await sut.GetResponseAsync([]);

        Assert.Equal("fallover", result.Text);
    }

    // GetResponseAsync — no fallover for AuthError by default

    [Fact]
    public async Task GetResponseAsync_DoesNotFallOver_WhenPrimaryThrowsAuthError()
    {
        var primary  = new FakeClient(() => throw new InvalidOperationException("Unauthorized — bad API key"));
        var fallover = new FakeClient(() => Task.FromResult(FakeResponse("fallover")));

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetResponseAsync([]));
        Assert.Equal(0, fallover.CallCount);
    }

    // GetResponseAsync — no fallover when reason not in FalloverOn

    [Fact]
    public async Task GetResponseAsync_DoesNotFallOver_WhenReasonNotInFalloverOn()
    {
        var onlyContextReasons = new HashSet<FailoverReason> { FailoverReason.ContextExceeded };
        var primary  = new FakeClient(() => throw new InvalidOperationException("HTTP 429 Too Many Requests"));
        var fallover = new FakeClient(() => Task.FromResult(FakeResponse("fallover")));

        using var sut = new FalloverChatClient([primary, fallover], onlyContextReasons);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetResponseAsync([]));
        Assert.Equal(0, fallover.CallCount);
    }

    // GetResponseAsync — all slots fail

    [Fact]
    public async Task GetResponseAsync_ThrowsLastException_WhenAllSlotsFail()
    {
        var p1 = new FakeClient(() => throw new InvalidOperationException("HTTP 429 Too Many Requests"));
        var p2 = new FakeClient(() => throw new InvalidOperationException("Service Unavailable"));

        using var sut = new FalloverChatClient([p1, p2], AllReasons);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetResponseAsync([]));
        Assert.Contains("Service Unavailable", ex.Message);
    }

    // GetResponseAsync — chain of 3

    [Fact]
    public async Task GetResponseAsync_TriesWholeChain_UntilOneSucceeds()
    {
        var p1 = new FakeClient(() => throw new InvalidOperationException("HTTP 429 Too Many Requests"));
        var p2 = new FakeClient(() => throw new InvalidOperationException("Service Unavailable"));
        var p3 = new FakeClient(() => Task.FromResult(FakeResponse("third")));

        using var sut = new FalloverChatClient([p1, p2, p3], AllReasons);
        var result = await sut.GetResponseAsync([]);
        Assert.Equal("third", result.Text);
    }

    // GetStreamingResponseAsync — success on first slot

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsFromFirstSlot_WhenPrimarySucceeds()
    {
        var primary  = new FakeStreamingClient(["a", "b"]);
        var fallover = new FakeStreamingClient(["x"]);

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        var chunks = await CollectStreamAsync(sut);

        Assert.Equal(["a", "b"], chunks);
        Assert.Equal(0, fallover.CallCount);
    }

    // GetStreamingResponseAsync — fallover before first chunk

    [Fact]
    public async Task GetStreamingResponseAsync_FallsOver_WhenPrimaryThrowsBeforeFirstChunk()
    {
        var primary  = new FakeStreamingClient(new InvalidOperationException("HTTP 429 Too Many Requests"));
        var fallover = new FakeStreamingClient(["ok"]);

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        var chunks = await CollectStreamAsync(sut);

        Assert.Equal(["ok"], chunks);
    }

    // GetStreamingResponseAsync — unclassifiable error on first slot does NOT fallover

    [Fact]
    public async Task GetStreamingResponseAsync_DoesNotFallOver_WhenReasonIsNone()
    {
        var primary  = new FakeStreamingClient(new InvalidOperationException("Something weird happened"));
        var fallover = new FakeStreamingClient(["ok"]);

        using var sut = new FalloverChatClient([primary, fallover], AllReasons);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectStreamAsync(sut));
        Assert.Equal(0, fallover.CallCount);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ChatResponse FakeResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private static async Task<List<string>> CollectStreamAsync(IChatClient client)
    {
        var results = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([]))
        {
            foreach (var c in update.Contents.OfType<TextContent>())
                results.Add(c.Text ?? string.Empty);
        }
        return results;
    }

    // Fake non-streaming client
    private sealed class FakeClient(Func<Task<ChatResponse>> responseFactory) : IChatClient
    {
        public int CallCount { get; private set; }

        public object? GetService(Type t, object? k) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
        {
            CallCount++;
            return responseFactory();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }
    }

    // Fake streaming client — either yields a sequence of text chunks, or throws on first MoveNext
    private sealed class FakeStreamingClient : IChatClient
    {
        private readonly string[]? _chunks;
        private readonly Exception? _throwOnFirst;
        public int CallCount { get; private set; }

        public FakeStreamingClient(string[] chunks) => _chunks = chunks;
        public FakeStreamingClient(Exception throwOnFirst) => _throwOnFirst = throwOnFirst;

        public object? GetService(Type t, object? k) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? opts,
            [EnumeratorCancellation] CancellationToken ct)
        {
            CallCount++;
            if (_throwOnFirst is not null)
                throw _throwOnFirst;

            foreach (var chunk in _chunks ?? [])
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }
}
