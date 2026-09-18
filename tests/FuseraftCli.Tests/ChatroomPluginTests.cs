using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ChatroomPlugin"/> — the shared append-only JSONL message log used
/// for agent-to-agent coordination. Each agent gets its own instance bound to its name, but
/// all instances sharing the same path see each other's messages.
/// </summary>
public sealed class ChatroomPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ChatroomPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_chatroom_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _path = Path.Combine(_root, "nested", "chatroom.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ChatroomPlugin NewPlugin(string agentName = "Writer") => new(agentName, _path);

    // ── SendAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_EmptyRecipient_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.SendAsync("", "hello");
        Assert.StartsWith("[ERROR]", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task SendAsync_EmptyMessage_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.SendAsync("Reviewer", "   ");
        Assert.StartsWith("[ERROR]", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task SendAsync_Valid_ReturnsConfirmationNamingRecipient()
    {
        var plugin = NewPlugin();
        var result = await plugin.SendAsync("Reviewer", "Please check the diff.");
        Assert.Contains("Reviewer", result);
    }

    [Fact]
    public async Task SendAsync_CreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_path)));

        var plugin = NewPlugin();
        await plugin.SendAsync("Reviewer", "hello");

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task SendAsync_AppendsOneLinePerMessage()
    {
        var plugin = NewPlugin();
        await plugin.SendAsync("Reviewer", "first");
        await plugin.SendAsync("Reviewer", "second");

        var lines = await File.ReadAllLinesAsync(_path);
        Assert.Equal(2, lines.Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    // ── ReadAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_NoFile_ReturnsEmptyMarker()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadAsync();
        Assert.StartsWith("[EMPTY]", result);
    }

    [Fact]
    public async Task ReadAsync_AfterSend_IncludesFromToAndMessage()
    {
        var writer = NewPlugin("Writer");
        await writer.SendAsync("Reviewer", "Please check the diff.");

        var reader = NewPlugin("Reviewer");
        var result = await reader.ReadAsync();

        Assert.Contains("Writer", result);
        Assert.Contains("Reviewer", result);
        Assert.Contains("Please check the diff.", result);
    }

    [Fact]
    public async Task ReadAsync_MultipleAgents_ShareSameFile()
    {
        var writer  = NewPlugin("Writer");
        var editor  = NewPlugin("Editor");

        await writer.SendAsync("All", "draft ready");
        await editor.SendAsync("All", "review started");

        var read = await writer.ReadAsync();
        Assert.Contains("draft ready", read);
        Assert.Contains("review started", read);
    }

    [Fact]
    public async Task ReadAsync_RespectsCountLimit_ReturnsOnlyMostRecent()
    {
        var plugin = NewPlugin();
        for (int i = 1; i <= 5; i++)
            await plugin.SendAsync("All", $"message-{i}");

        var result = await plugin.ReadAsync(count: 2);

        Assert.DoesNotContain("message-1", result);
        Assert.DoesNotContain("message-3", result);
        Assert.Contains("message-4", result);
        Assert.Contains("message-5", result);
    }

    [Fact]
    public async Task ReadAsync_ZeroOrNegativeCount_StillReturnsAtLeastOneMessage()
    {
        var plugin = NewPlugin();
        await plugin.SendAsync("All", "only message");

        var result = await plugin.ReadAsync(count: 0);

        Assert.Contains("only message", result);
    }

    [Fact]
    public async Task ReadAsync_SkipsMalformedLinesWithoutThrowing()
    {
        var plugin = NewPlugin();
        await plugin.SendAsync("All", "valid message");

        // Inject a corrupt line between valid ones.
        await File.AppendAllTextAsync(_path, "{not valid json\n");
        await plugin.SendAsync("All", "another valid message");

        var result = await plugin.ReadAsync();

        Assert.Contains("valid message", result);
        Assert.Contains("another valid message", result);
    }

    [Fact]
    public async Task ReadAsync_IncludesHeaderWithMessageCount()
    {
        var plugin = NewPlugin();
        await plugin.SendAsync("All", "one");
        await plugin.SendAsync("All", "two");

        var result = await plugin.ReadAsync();

        Assert.Contains("2 message(s)", result);
    }
}
