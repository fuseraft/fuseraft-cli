using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Images;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Images across a session's life: the sidecar store, snapshot save/restore (the reason images are not
/// embedded in the snapshot itself), and the <c>/image</c> and <c>/retry</c> commands.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplImageSessionTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-imgsess-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplImageSessionTests()
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

    private static DataContent Img(string name = "a.png", byte[]? bytes = null)
    {
        ImageAttachments.TryCreate(bytes ?? ImageAttachmentsTests.Png(), name, out var img, out _);
        return img!;
    }

    // ── Store ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Store_RoundTrips_AndIsContentAddressed()
    {
        var bytes = ImageAttachmentsTests.Png(200);

        var id1 = ReplImageStore.Save(bytes, "image/png");
        var id2 = ReplImageStore.Save(bytes, "image/png");

        Assert.NotNull(id1);
        Assert.Equal(id1, id2);                                   // same bytes → one file
        Assert.Single(Directory.GetFiles(ReplImageStore.Root));
        Assert.Equal(bytes, ReplImageStore.Load(id1));
        Assert.EndsWith(".png", id1);
    }

    [Fact]
    public void Store_DifferentBytes_DifferentIds_AndLeavesNoTempFiles()
    {
        var a = ReplImageStore.Save(ImageAttachmentsTests.Png(10), "image/png");
        var b = ReplImageStore.Save(ImageAttachmentsTests.Jpeg(10), "image/jpeg");

        Assert.NotEqual(a, b);
        Assert.EndsWith(".jpg", b);
        Assert.DoesNotContain(Directory.GetFiles(ReplImageStore.Root), f => f.EndsWith(".tmp"));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("0123456789abcdef01234567.png/../x")]
    [InlineData("0123456789ABCDEF01234567.png")]      // uppercase: never produced by Save
    [InlineData("0123456789abcdef0123.png")]           // wrong length
    [InlineData("0123456789abcdef01234567.exe")]
    [InlineData("0123456789abcdef01234567")]
    [InlineData("")]
    [InlineData(null)]
    public void Store_RejectsAnythingThatIsNotAGeneratedId_SoASnapshotCannotTraverse(string? id)
    {
        Assert.False(ReplImageStore.IsValidId(id));
        Assert.Null(ReplImageStore.Load(id));
    }

    [Fact]
    public void Store_MissingFile_LoadsAsNull()
    {
        Assert.Null(ReplImageStore.Load("0123456789abcdef01234567.png"));
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_RoundTripsImages_AndDoesNotEmbedTheirBytes()
    {
        var bytes = ImageAttachmentsTests.Png(300_000);           // 300 KB — would be ~400 KB of base64 if embedded
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, [new TextContent("look"), Img("bug.png", bytes)]),
            new(ChatRole.Assistant, "I see it."),
        };

        var snap = ReplSessionSnapshot.Capture("s-img", "m", "/tmp", 1, history, DateTime.UtcNow);
        await ReplSessionSnapshot.SaveAsync(snap);
        var file = Directory.GetFiles(FuseraftPaths.GlobalReplSessions, "repl-s-img.json").Single();

        Assert.True(new FileInfo(file).Length < 5_000, "snapshot must hold a reference, not the pixels");
        Assert.DoesNotContain(Convert.ToBase64String(bytes[..64]), File.ReadAllText(file));

        var restored = (await ReplSessionSnapshot.LoadAsync("s-img"))!.RestoreHistory();
        var user = restored[1];
        var image = Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(bytes, image.Data.ToArray());
        Assert.Equal("bug.png", image.AdditionalProperties![ImageAttachments.NameProperty]);
        Assert.Equal("look", ImageAttachments.UserText(user));
    }

    [Fact]
    public async Task Snapshot_ImageOnlyMessage_SurvivesRestore()
    {
        // A message with no text used to vanish on restore (non-text content serialized as "skip").
        var history = new List<ChatMessage> { new(ChatRole.User, [Img()]) };

        var snap = ReplSessionSnapshot.Capture("s-only", "m", "/tmp", 1, history, DateTime.UtcNow);
        await ReplSessionSnapshot.SaveAsync(snap);

        var restored = (await ReplSessionSnapshot.LoadAsync("s-only"))!.RestoreHistory();
        Assert.Single(restored);
        Assert.Equal(1, ImageAttachments.CountImages(restored[0]));
    }

    [Fact]
    public async Task Snapshot_WhenTheStoreEntryIsGone_RestoresAPlaceholder_NotAHoleOrACrash()
    {
        var snap = ReplSessionSnapshot.Capture("s-gone", "m", "/tmp", 1,
            [new ChatMessage(ChatRole.User, [new TextContent("look"), Img("lost.png")])], DateTime.UtcNow);
        await ReplSessionSnapshot.SaveAsync(snap);
        foreach (var f in Directory.GetFiles(ReplImageStore.Root)) File.Delete(f);

        var restored = (await ReplSessionSnapshot.LoadAsync("s-gone"))!.RestoreHistory();

        Assert.Equal(0, ImageAttachments.CountImages(restored[0]));
        Assert.Contains("[image no longer available: lost.png]", restored[0].Text);
    }

    [Fact]
    public void Snapshot_HostileImageId_IsTreatedAsMissing()
    {
        var content = new ReplSerializedContent { Type = "image", MediaType = "image/png", ImageId = "../../../../etc/hostname", ImageName = "x.png" };

        var restored = content.Restore();

        var text = Assert.IsType<TextContent>(restored);
        Assert.Contains("no longer available", text.Text);
    }

    [Fact]
    public async Task Snapshot_DoesNotRewriteTheStoreOnEveryTurn()
    {
        // The snapshot is rewritten after each turn; the cached id must spare re-hashing/re-writing the image.
        var image = Img("once.png", ImageAttachmentsTests.Png(1000));
        var history = new List<ChatMessage> { new(ChatRole.User, [new TextContent("x"), image]) };

        await ReplSessionSnapshot.SaveAsync(ReplSessionSnapshot.Capture("s-cache", "m", "/tmp", 1, history, DateTime.UtcNow));
        var stored = Directory.GetFiles(ReplImageStore.Root).Single();
        var firstWrite = File.GetLastWriteTimeUtc(stored);
        Assert.True(image.AdditionalProperties!.ContainsKey(ImageAttachments.IdProperty));

        await Task.Delay(50);
        await ReplSessionSnapshot.SaveAsync(ReplSessionSnapshot.Capture("s-cache", "m", "/tmp", 2, history, DateTime.UtcNow));

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(stored));
    }

    // ── Commands ────────────────────────────────────────────────────────────────

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(string cwd)
    {
        var events = Path.Combine(_home, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: cwd, sessionId: "img-cmd", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(events), eventsPath: events,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_home, "mem")),
            toolsByCategory: [], systemPrompt: "sys", pendingSave: false, adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public async Task ImageCommand_SendsTheMessageWithItsImage()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_home, "proj")).FullName;
        File.WriteAllBytes(Path.Combine(cwd, "shot.png"), ImageAttachmentsTests.Png());
        var ctx = NewContext(cwd);

        var r = await ReplCommands.HandleAsync(ctx, "/image", "shot.png why is this red?", CancellationToken.None);

        Assert.Equal(CommandOutcome.SendInput, r.Outcome);
        Assert.Equal("why is this red?", r.InputOverride);
        var img = Assert.Single(r.Attachments!);
        Assert.Equal("image/png", img.MediaType);
    }

    [Fact]
    public async Task ImageCommand_WithATypoedPath_SendsNothing()
    {
        var ctx = NewContext(Directory.CreateDirectory(Path.Combine(_home, "proj2")).FullName);

        var r = await ReplCommands.HandleAsync(ctx, "/image", "typo.png what is this", CancellationToken.None);

        Assert.Equal(CommandOutcome.Continue, r.Outcome);
        Assert.Null(r.InputOverride);
    }

    [Fact]
    public async Task Retry_ResendsTheImageToo_NotJustTheWords()
    {
        var ctx = NewContext(_home);
        ctx.History.Add(new ChatMessage(ChatRole.User, [new TextContent("what is this?"), Img("keep.png")]));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "A cat."));

        var r = await ReplCommands.HandleAsync(ctx, "/retry", "", CancellationToken.None);

        Assert.Equal("what is this?", r.InputOverride);
        Assert.Equal("keep.png", Assert.Single(r.Attachments!).AdditionalProperties![ImageAttachments.NameProperty]);
        Assert.Single(ctx.History);                                // only the system prompt remains
    }

    [Fact]
    public async Task Retry_AfterOlderImagesWereStripped_DoesNotDragPlaceholderTextAlong()
    {
        var ctx = NewContext(_home);
        ctx.History.Add(new ChatMessage(ChatRole.User, [new TextContent("first?"), Img("old.png")]));
        ctx.History.Add(new ChatMessage(ChatRole.User, [new TextContent("second?"), Img("new.png")]));
        ImageAttachments.StripOlderImages(ctx.History, keep: 0);   // both become placeholders

        var r = await ReplCommands.HandleAsync(ctx, "/retry", "", CancellationToken.None);

        Assert.Equal("second?", r.InputOverride);                  // not "second?\n[image omitted …]"
        Assert.Empty(r.Attachments!);
    }
}
