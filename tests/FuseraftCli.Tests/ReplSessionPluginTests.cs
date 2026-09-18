using fuseraft.Core;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReplSessionPlugin"/> — REPL session metadata, context-status/compact
/// delegate wiring, and diagnostic log-file access. Isolated from the real
/// <c>~/.fuseraft</c> tree via the <c>FUSERAFT_HOME</c> override (see
/// <see cref="FuseraftPathsHomeOverrideTests"/>) since <see cref="ReplSessionPlugin.ListAsync"/>,
/// <see cref="ReplSessionPlugin.ReadEventLogAsync"/>, and <see cref="ReplSessionPlugin.ReadLogAsync"/>
/// all resolve paths under the global fuseraft root.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplSessionPluginTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home;
    private readonly string _cwd;
    private readonly string _slug;
    private const string SessionId = "abcd1234";
    private static readonly DateTime StartedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public ReplSessionPluginTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "fuseraft_replsession_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);

        _cwd  = Path.Combine(Path.GetTempPath(), "fake-project");
        _slug = FuseraftPaths.ProjectSlug(_cwd);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private ReplSessionPlugin NewPlugin() => new(SessionId, StartedAt, "grok-4.5", _cwd);

    // ── CompactContextAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CompactContextAsync_NoDelegateSet_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.CompactContextAsync();
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task CompactContextAsync_DelegateSet_InvokesDelegateAndReturnsItsResult()
    {
        var plugin = NewPlugin();
        string? capturedFocus = null;
        plugin.SetCompactDelegate((focus, ct) =>
        {
            capturedFocus = focus;
            return Task.FromResult("[OK] Compacted.");
        });

        var result = await plugin.CompactContextAsync("fix build error");

        Assert.Equal("[OK] Compacted.", result);
        Assert.Equal("fix build error", capturedFocus);
    }

    // ── GetContextStatus ─────────────────────────────────────────────────────

    [Fact]
    public void GetContextStatus_NoDelegateSet_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = plugin.GetContextStatus();
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void GetContextStatus_DelegateSet_FormatsTokensAndPercentage()
    {
        var plugin = NewPlugin();
        plugin.SetStatusDelegate(() => (EstimatedTokens: 40_000, Budget: 80_000, TurnIndex: 3));

        var result = plugin.GetContextStatus();

        Assert.Contains("40,000", result);
        Assert.Contains("80,000", result);
        // "50.0 %" vs "50.0%" spacing is culture-dependent (.NET's "P1" format varies by
        // ICU data) — assert the number, not the exact percent-sign spacing.
        Assert.Contains("50.0", result);
        Assert.Contains("turn:             3", result);
    }

    // ── Current ──────────────────────────────────────────────────────────────

    [Fact]
    public void Current_IncludesSessionIdModelAndCwd()
    {
        var plugin = NewPlugin();
        var result = plugin.Current();

        Assert.Contains(SessionId, result);
        Assert.Contains("grok-4.5", result);
        Assert.Contains(_cwd, result);
    }

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_NoSavedSessions_ReturnsInfoMessage()
    {
        var plugin = NewPlugin();
        var result = await plugin.ListAsync();
        Assert.StartsWith("[INFO]", result);
    }

    // ── ReadEventLogAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadEventLogAsync_NoLogFile_ReturnsInfoMessage()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadEventLogAsync();
        Assert.StartsWith("[INFO]", result);
        Assert.Contains(SessionId, result);
    }

    [Fact]
    public async Task ReadEventLogAsync_WithLogFile_ReturnsMatchingTail()
    {
        var logPath = FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalReplEventsLog, SessionId, _slug);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllLinesAsync(logPath, ["event-1", "event-2", "event-3"]);

        var plugin = NewPlugin();
        var result = await plugin.ReadEventLogAsync(maxLines: 2);

        Assert.DoesNotContain("event-1", result);
        Assert.Contains("event-2", result);
        Assert.Contains("event-3", result);
    }

    [Fact]
    public async Task ReadEventLogAsync_ExplicitTargetSessionId_ReadsThatSessionsLog()
    {
        const string otherSessionId = "other999";
        var logPath = FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalReplEventsLog, otherSessionId, _slug);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllLinesAsync(logPath, ["other-session-event"]);

        var plugin = NewPlugin();
        var result = await plugin.ReadEventLogAsync(targetSessionId: otherSessionId);

        Assert.Contains("other-session-event", result);
    }

    // ── ReadLogAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadLogAsync_UnknownLogName_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadLogAsync("nonsense");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Unknown log", result);
    }

    [Fact]
    public async Task ReadLogAsync_ValidNameButFileMissing_ReturnsInfoMessage()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadLogAsync("app");
        Assert.StartsWith("[INFO]", result);
    }

    [Fact]
    public async Task ReadLogAsync_ValidNameWithContent_ReturnsTail()
    {
        var logPath = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAppLog, _slug);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllLinesAsync(logPath, ["line-1", "line-2"]);

        var plugin = NewPlugin();
        var result = await plugin.ReadLogAsync("app", maxLines: 100);

        Assert.Contains("line-1", result);
        Assert.Contains("line-2", result);
    }

    [Fact]
    public async Task ReadLogAsync_NameIsCaseInsensitive()
    {
        var logPath = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAppLog, _slug);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllLinesAsync(logPath, ["line-1"]);

        var plugin = NewPlugin();
        var result = await plugin.ReadLogAsync("APP");

        Assert.Contains("line-1", result);
    }
}
