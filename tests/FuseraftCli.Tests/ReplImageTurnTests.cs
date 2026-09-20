using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Images;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Drives a real <see cref="ReplTurn.ExecuteAsync"/> turn with images to pin what the session-level
/// contract promises: the model receives the picture, history keeps it (bounded), and the event log
/// records that images were sent — never their bytes.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplImageTurnTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-imgturn-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplImageTurnTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        foreach (var c in _contexts) { c.Emitter.Dispose(); c.Factory.Dispose(); }
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Answers every request with "saw N image(s)" and remembers exactly what it was sent.</summary>
    private sealed class SeeingClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var sent = messages.ToList();
            Requests.Add(sent);
            var images = sent.Sum(ImageAttachments.CountImages);
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new TextContent($"saw {images} image(s)"), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            };
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private (ReplSessionContext Ctx, SeeingClient Client) NewContext()
    {
        var client = new SeeingClient();
        var events = Path.Combine(_home, "events.jsonl");
        var ctx = new ReplSessionContext(
            cwd: _home, sessionId: "img-turn", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(events), eventsPath: events,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_home, "mem")),
            toolsByCategory: [], systemPrompt: "sys", pendingSave: false, adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return (ctx, client);
    }

    private static DataContent Img(string name, int seed)
    {
        // Distinct bytes per image so content-addressing does not merge them.
        var bytes = ImageAttachmentsTests.Png(64);
        bytes[^1] = (byte)seed;
        ImageAttachments.TryCreate(bytes, name, out var img, out _);
        return img!;
    }

    private static Task<bool> Turn(ReplSessionContext ctx, string text, params DataContent[] images) =>
        ReplTurn.ExecuteAsync(ctx, text, isStepRequest: false, capturePlan: false, activeStep: null,
            CancellationToken.None, attachments: images);

    [Fact]
    public async Task TheModelReceivesTheImage_AlongsideTheText()
    {
        var (ctx, client) = NewContext();

        Assert.True(await Turn(ctx, "what is this?", Img("a.png", 1)));

        var user = client.Requests.Single().Single(m => m.Role == ChatRole.User);
        Assert.Equal("what is this?", ImageAttachments.UserText(user));
        Assert.Equal(1, ImageAttachments.CountImages(user));
        Assert.Contains("saw 1 image(s)", ctx.History[^1].Text);
    }

    [Fact]
    public async Task ATurnWithoutImages_IsExactlyAsBefore()
    {
        var (ctx, client) = NewContext();

        await Turn(ctx, "plain question");

        var user = client.Requests.Single().Single(m => m.Role == ChatRole.User);
        Assert.Single(user.Contents);
        Assert.Equal("plain question", user.Text);
    }

    [Fact]
    public async Task OnlyTheMostRecentImagesStayAttached_AcrossTurns()
    {
        var (ctx, client) = NewContext();
        var total = ImageAttachments.KeepRecentImages + 3;

        for (var i = 1; i <= total; i++)
            await Turn(ctx, $"question {i}", Img($"shot{i}.png", i));

        // What the model was sent on the last turn, and what history holds now, are both bounded.
        Assert.Equal(ImageAttachments.KeepRecentImages, client.Requests[^1].Sum(ImageAttachments.CountImages));
        Assert.Equal(ImageAttachments.KeepRecentImages, ctx.History.Sum(ImageAttachments.CountImages));

        var users = ctx.History.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal(0, ImageAttachments.CountImages(users[0]));                         // oldest: stripped…
        Assert.Contains("[image omitted from context: shot1.png", users[0].Text);         // …but still says what it was
        Assert.Equal("question 1", ImageAttachments.UserText(users[0]));
        Assert.Equal(1, ImageAttachments.CountImages(users[^1]));                        // newest: intact
    }

    [Fact]
    public async Task TheEventLogRecordsHowManyImages_NeverTheirBytes()
    {
        var (ctx, _) = NewContext();
        var img = Img("secret-screenshot.png", 9);
        var b64Fragment = Convert.ToBase64String(img.Data.ToArray()[..24]);

        await Turn(ctx, "look", img, Img("b.png", 10));
        ctx.Emitter.Dispose();

        var log = File.ReadAllText(ctx.EventsPath);
        Assert.Contains("\"images\":2", log);
        Assert.DoesNotContain(b64Fragment, log);
        Assert.DoesNotContain("data:image", log);
    }

    [Fact]
    public async Task AFailedTurn_DoesNotLeaveTheRejectedImageInHistory()
    {
        var (ctx, _) = NewContext();
        ctx.Client = new ThrowingClient(new HttpRequestException("bad request", null, System.Net.HttpStatusCode.BadRequest));

        var ok = await Turn(ctx, "look", Img("a.png", 1));

        Assert.False(ok);
        Assert.Equal(0, ctx.History.Sum(ImageAttachments.CountImages));   // a poisoned message must not be resent every turn
    }

    private sealed class ThrowingClient(Exception ex) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw ex;
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw ex;
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
