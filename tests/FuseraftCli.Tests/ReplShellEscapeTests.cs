using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the `!&lt;command&gt;` shell escape's pure, testable pieces: the `cd` special-case
/// (TryHandleCd inside ReplShellEscape — cd's own directory change never outlives the child
/// process it would otherwise run in, so it's handled directly against ReplSessionContext
/// instead of being spawned) and the "!!" repeat-last-command detection. Actually spawning a
/// shell (RunInheritedAsync) inherits this process's console handles directly and isn't
/// meaningfully unit-testable, so it's exercised manually instead — see the REPL manual test
/// pass this shipped with.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplShellEscapeTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplShellEscapeTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);

        foreach (var ctx in _contexts)
        {
            ctx.Emitter.Dispose();
            ctx.Factory.Dispose();
        }
    }

    private sealed class NoopChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(string cwd)
    {
        var eventsPath = Path.Combine(_tempHome, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: cwd, sessionId: "shell-escape-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: new(StringComparer.OrdinalIgnoreCase), systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true; // avoid touching the real console during tests
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public void IsShellEscape_RequiresLeadingBang()
    {
        Assert.True(ReplShellEscape.IsShellEscape("!git status"));
        Assert.True(ReplShellEscape.IsShellEscape("!!"));
        Assert.False(ReplShellEscape.IsShellEscape("git status"));
        Assert.False(ReplShellEscape.IsShellEscape(""));
        Assert.False(ReplShellEscape.IsShellEscape("/help"));
    }

    [Fact]
    public async Task ShellCwd_StartsAtSessionCwd()
    {
        var ctx = NewContext(_tempHome);
        Assert.Equal(_tempHome, ctx.ShellCwd);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CdWithRelativePath_UpdatesShellCwdAndPersists()
    {
        var ctx = NewContext(_tempHome);
        var sub = Path.Combine(_tempHome, "sub");
        Directory.CreateDirectory(sub);

        await ReplShellEscape.RunAsync(ctx, "!cd sub", CancellationToken.None);

        Assert.Equal(Path.GetFullPath(sub), ctx.ShellCwd);
        Assert.Equal(_tempHome, ctx.PrevShellCwd);
    }

    [Fact]
    public async Task CdWithNoArgs_GoesToHomeDirectory()
    {
        var ctx = NewContext(_tempHome);

        await ReplShellEscape.RunAsync(ctx, "!cd", CancellationToken.None);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ctx.ShellCwd);
    }

    [Fact]
    public async Task CdDash_ReturnsToPreviousDirectory()
    {
        var ctx = NewContext(_tempHome);
        var sub = Path.Combine(_tempHome, "sub");
        Directory.CreateDirectory(sub);

        await ReplShellEscape.RunAsync(ctx, "!cd sub", CancellationToken.None);
        Assert.Equal(Path.GetFullPath(sub), ctx.ShellCwd);

        await ReplShellEscape.RunAsync(ctx, "!cd -", CancellationToken.None);
        Assert.Equal(_tempHome, ctx.ShellCwd);
    }

    [Fact]
    public async Task CdToNonexistentDirectory_LeavesShellCwdUnchanged()
    {
        var ctx = NewContext(_tempHome);

        await ReplShellEscape.RunAsync(ctx, "!cd does-not-exist", CancellationToken.None);

        Assert.Equal(_tempHome, ctx.ShellCwd);
        Assert.Null(ctx.PrevShellCwd);
    }

    [Fact]
    public async Task BangBang_WithNoPriorCommand_DoesNotThrow()
    {
        var ctx = NewContext(_tempHome);

        await ReplShellEscape.RunAsync(ctx, "!!", CancellationToken.None);

        Assert.Null(ctx.LastShellCommand);
    }

    [Fact]
    public async Task Cd_RecordsAsLastShellCommand_SoBangBangRepeatsIt()
    {
        var ctx = NewContext(_tempHome);
        var sub = Path.Combine(_tempHome, "sub");
        Directory.CreateDirectory(sub);

        await ReplShellEscape.RunAsync(ctx, "!cd sub", CancellationToken.None);
        Assert.Equal("cd sub", ctx.LastShellCommand);

        // Move back out, then repeat the recorded "cd sub" via "!!" — should land in sub again.
        ctx.ShellCwd = _tempHome;
        await ReplShellEscape.RunAsync(ctx, "!!", CancellationToken.None);

        Assert.Equal(Path.GetFullPath(sub), ctx.ShellCwd);
    }

    [Fact]
    public async Task EmptyBang_PrintsUsageAndDoesNotThrow()
    {
        var ctx = NewContext(_tempHome);

        await ReplShellEscape.RunAsync(ctx, "!", CancellationToken.None);

        Assert.Null(ctx.LastShellCommand);
    }
}
