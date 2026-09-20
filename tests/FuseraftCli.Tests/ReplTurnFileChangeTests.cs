using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression: the turn's file-change list (the <c>A/M/D path</c> lines under the status bar and the
/// <c>file_changes</c> event) was built from the tool <em>call</em>, so a write the user denied in HITL —
/// or one that errored — was still reported as modified, though the file was untouched.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnFileChangeTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-fchg-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnFileChangeTests()
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

    // ── DescribeFileChange / CommitFileChange ───────────────────────────────────

    private static IDictionary<string, object?> Args(string key, string value) => new Dictionary<string, object?> { [key] = value };

    [Theory]
    [InlineData("write_file",       "path",        'A')]   // file does not exist yet
    [InlineData("patch_file",       "path",        'A')]
    [InlineData("create_directory", "path",        'A')]
    [InlineData("delete_file",      "path",        'D')]
    [InlineData("delete_directory", "path",        'D')]
    [InlineData("copy_file",        "destination", 'A')]
    [InlineData("move_file",        "destination", 'M')]
    public void DescribeFileChange_ClassifiesTheMutatingTools(string tool, string argName, char sigil)
    {
        var d = ReplTurn.DescribeFileChange(tool, Args(argName, "out/x.txt"), _home);

        Assert.Equal((sigil, Path.Combine("out", "x.txt")), d);
    }

    [Fact]
    public void DescribeFileChange_ExistingFileIsModified_NotAdded()
    {
        File.WriteAllText(Path.Combine(_home, "exists.txt"), "x");

        Assert.Equal('M', ReplTurn.DescribeFileChange("write_file", Args("path", "exists.txt"), _home)!.Value.Sigil);
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("shell_run")]
    [InlineData("git_commit")]
    public void DescribeFileChange_IgnoresEveryOtherTool(string tool) =>
        Assert.Null(ReplTurn.DescribeFileChange(tool, Args("path", "a.txt"), _home));

    [Fact]
    public void DescribeFileChange_NoPath_IsNull() =>
        Assert.Null(ReplTurn.DescribeFileChange("write_file", new Dictionary<string, object?>(), _home));

    private static (List<(char, string)> Changes, HashSet<string> Seen, Dictionary<string, (char, string)> Pending) Fresh() =>
        ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);

    [Theory]
    [InlineData("[DENIED] Action blocked by user.")]
    [InlineData("[ERROR] patch matched 3 places")]
    [InlineData("[FAIL] could not write")]
    [InlineData("[NOT FOUND] no such directory")]
    public void CommitFileChange_FailedResult_ReportsNothing(string resultText)
    {
        var (changes, seen, pending) = Fresh();
        pending["c1"] = ('M', "a.txt");

        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c1", resultText), changes, seen);

        Assert.Empty(changes);
        Assert.Empty(pending);   // consumed either way — a stale entry must not leak into a later call
    }

    [Fact]
    public void CommitFileChange_ThrownTool_ReportsNothing()
    {
        var (changes, seen, pending) = Fresh();
        pending["c1"] = ('A', "a.txt");

        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c1", null) { Exception = new IOException("disk full") }, changes, seen);

        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("[OK] wrote 12 bytes")]
    [InlineData("wrote a.txt")]
    public void CommitFileChange_SuccessfulResult_IsReported(string resultText)
    {
        var (changes, seen, pending) = Fresh();
        pending["c1"] = ('M', "a.txt");

        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c1", resultText), changes, seen);

        Assert.Equal([('M', "a.txt")], changes);
    }

    [Fact]
    public void CommitFileChange_ResultForANonFileCall_IsANoOp()
    {
        var (changes, seen, pending) = Fresh();

        ReplTurn.CommitFileChange(pending, new FunctionResultContent("unrelated", "[OK]"), changes, seen);

        Assert.Empty(changes);
    }

    [Fact]
    public void CommitFileChange_DeniedThenRetriedAndSucceeded_ReportsItOnce()
    {
        var (changes, seen, pending) = Fresh();

        pending["c1"] = ('M', "a.txt");
        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c1", "[DENIED] no"), changes, seen);
        pending["c2"] = ('M', "a.txt");
        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c2", "[OK]"), changes, seen);
        pending["c3"] = ('M', "A.TXT");
        ReplTurn.CommitFileChange(pending, new FunctionResultContent("c3", "[OK]"), changes, seen);   // same file again: deduped

        Assert.Single(changes);
    }

    [Fact]
    public void CommitFileChange_ProvidersThatSendAnEmptyCallId_StillMatchTheirCall()
    {
        // MEAI forbids a null call id; some providers send "" — call and result must still pair up.
        var (changes, seen, pending) = Fresh();
        pending[string.Empty] = ('A', "n.txt");

        ReplTurn.CommitFileChange(pending, new FunctionResultContent("", "[OK]"), changes, seen);

        Assert.Single(changes);
    }

    // ── Through a real turn ─────────────────────────────────────────────────────

    /// <summary>Streams the given (call, result) pairs — as the middleware would surface them — then a closing text chunk.</summary>
    private sealed class ScriptedClient((string Tool, string Path, string Result)[] steps) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var (tool, path, result) = steps[i];
                yield return new ChatResponseUpdate
                {
                    Role     = ChatRole.Assistant,
                    Contents = [new FunctionCallContent($"call-{i}", tool, new Dictionary<string, object?> { ["path"] = path })],
                };
                await Task.Yield();
                yield return new ChatResponseUpdate
                {
                    Role     = ChatRole.Assistant,
                    Contents = [new FunctionResultContent($"call-{i}", result), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
                };
                await Task.Yield();
            }
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new TextContent("Done."), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            };
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private async Task<string> RunTurnAndReadEvents((string Tool, string Path, string Result)[] steps)
    {
        var events = Path.Combine(_home, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: _home, sessionId: "fchg", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new ScriptedClient(steps), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(events), eventsPath: events,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_home, "mem")),
            toolsByCategory: [], systemPrompt: "sys", pendingSave: false, adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);

        await ReplTurn.ExecuteAsync(ctx, "write it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);
        ctx.Emitter.Dispose();
        return File.ReadAllText(events);
    }

    [Fact]
    public async Task ADeniedWrite_IsNotReportedAsAFileChange()
    {
        var log = await RunTurnAndReadEvents([("patch_file", "textstats.py", "[DENIED] Action blocked by user.")]);

        Assert.DoesNotContain("\"file_changes\"", log);
    }

    [Fact]
    public async Task AnErroredWrite_IsNotReportedAsAFileChange()
    {
        var log = await RunTurnAndReadEvents([("write_file", "x.txt", "[ERROR] Path is outside the sandbox.")]);

        Assert.DoesNotContain("\"file_changes\"", log);
    }

    [Fact]
    public async Task ASuccessfulWrite_IsReported()
    {
        var log = await RunTurnAndReadEvents([("write_file", "made.txt", "[OK] wrote 5 bytes")]);

        Assert.Contains("\"file_changes\"", log);
        Assert.Contains("made.txt", log);
    }

    [Fact]
    public async Task InAMixedTurn_OnlyTheWritesThatHappenedAreReported()
    {
        var log = await RunTurnAndReadEvents([
            ("write_file", "denied.txt",  "[DENIED] Action blocked by user."),
            ("write_file", "kept.txt",    "[OK] wrote 3 bytes"),
            ("delete_file", "errored.txt", "[ERROR] no such file"),
        ]);

        var change = log.Split('\n').Single(l => l.Contains("\"file_changes\""));
        Assert.Contains("kept.txt", change);
        Assert.DoesNotContain("denied.txt", change);
        Assert.DoesNotContain("errored.txt", change);
    }
}
