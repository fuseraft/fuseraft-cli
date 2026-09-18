using fuseraft.Core;
using fuseraft.Infrastructure.Plugins;
using Moq;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="DecisionPlugin"/> — the agent-facing Architecture Decision Registry
/// tools (search/read/create/supersede). Uses a real, temp-directory-backed
/// <see cref="AdrRegistry"/>/<see cref="AdrStore"/> pair (no mocking needed — both are cheap,
/// file-backed, and this exercises the real read/write round trip) plus a mocked
/// <see cref="IKnowledgeLayer"/> for the "routes through the knowledge layer instead of the
/// registry directly" branch.
/// </summary>
public sealed class DecisionPluginTests : IDisposable
{
    private readonly string _root;
    private readonly AdrStore _store;
    private readonly AdrRegistry _registry;

    public DecisionPluginTests()
    {
        _root     = Path.Combine(Path.GetTempPath(), "fuseraft_decision_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _store    = new AdrStore(_root);
        _registry = new AdrRegistry(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DecisionPlugin NewPlugin(IKnowledgeLayer? knowledgeLayer = null) => new(_registry, knowledgeLayer);

    // ── SearchAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_NoEntries_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        var result = await plugin.SearchAsync();
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task SearchAsync_MatchingQuery_ReturnsFormattedList()
    {
        var plugin = NewPlugin();
        await plugin.CreateAsync("Use Postgres", "Need durable storage", "Adopt Postgres", tags: "persistence");

        var result = await plugin.SearchAsync("Postgres");

        Assert.Contains("Use Postgres", result);
        Assert.Contains("1 result(s)", result);
    }

    [Fact]
    public async Task SearchAsync_NonMatchingQuery_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        await plugin.CreateAsync("Use Postgres", "Need durable storage", "Adopt Postgres");

        var result = await plugin.SearchAsync("Kubernetes");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task SearchAsync_FiltersByTag()
    {
        var plugin = NewPlugin();
        await plugin.CreateAsync("Use Postgres", "ctx", "decision", tags: "persistence");
        await plugin.CreateAsync("Use OAuth", "ctx", "decision", tags: "security");

        var result = await plugin.SearchAsync(tag: "security");

        Assert.Contains("Use OAuth", result);
        Assert.DoesNotContain("Use Postgres", result);
    }

    // ── ReadAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_EmptyId_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadAsync("");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task ReadAsync_UnknownId_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        var result = await plugin.ReadAsync("ADR-9999");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task ReadAsync_KnownId_ReturnsFullEntryWithContextAndDecision()
    {
        var plugin = NewPlugin();
        var created = await plugin.CreateAsync(
            "Use Postgres", "Need durable storage", "Adopt Postgres",
            alternatives: "MySQL,SQLite", consequences: "Operational overhead");
        var id = ExtractId(created);

        var result = await plugin.ReadAsync(id);

        Assert.Contains("Use Postgres", result);
        Assert.Contains("Need durable storage", result);
        Assert.Contains("Adopt Postgres", result);
        Assert.Contains("MySQL", result);
        Assert.Contains("Operational overhead", result);
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "context", "decision")]
    [InlineData("title", "", "decision")]
    [InlineData("title", "context", "")]
    public async Task CreateAsync_MissingRequiredField_ReturnsError(string title, string context, string decision)
    {
        var plugin = NewPlugin();
        var result = await plugin.CreateAsync(title, context, decision);
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task CreateAsync_Valid_NoKnowledgeLayer_PersistsDirectlyToRegistry()
    {
        var plugin = NewPlugin();
        var created = await plugin.CreateAsync("Use Postgres", "ctx", "decision");
        var id = ExtractId(created);

        var entry = await _registry.GetByIdAsync(id);

        Assert.NotNull(entry);
        Assert.Equal("Use Postgres", entry!.Title);
    }

    [Fact]
    public async Task CreateAsync_Valid_WithKnowledgeLayer_RoutesThroughKnowledgeLayerNotRegistry()
    {
        var mockLayer = new Mock<IKnowledgeLayer>();
        mockLayer
            .Setup(k => k.RecordDecisionAsync(It.IsAny<AdrEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdrEntry e, CancellationToken _) => e);
        var plugin = NewPlugin(mockLayer.Object);

        var created = await plugin.CreateAsync("Use Postgres", "ctx", "decision");
        var id = ExtractId(created);

        mockLayer.Verify(k => k.RecordDecisionAsync(It.IsAny<AdrEntry>(), It.IsAny<CancellationToken>()), Times.Once);
        // The knowledge layer owns persistence in this path — the plugin must not also
        // write directly through the registry (that would double-write in production).
        Assert.Null(await _registry.GetByIdAsync(id));
    }

    [Fact]
    public async Task CreateAsync_WithSupersedes_MarksSupersededEntryStatus()
    {
        var plugin = NewPlugin();
        var firstId = ExtractId(await plugin.CreateAsync("Use MySQL", "ctx", "decision"));

        await plugin.CreateAsync("Use Postgres", "ctx", "decision", supersedes: firstId);

        var original = await plugin.ReadAsync(firstId);
        Assert.Contains("Status: Superseded", original);
    }

    [Fact]
    public async Task CreateAsync_TrimsWhitespaceFromTitle()
    {
        var plugin = NewPlugin();
        var created = await plugin.CreateAsync("  Use Postgres  ", "ctx", "decision");
        var id = ExtractId(created);

        var entry = await _registry.GetByIdAsync(id);

        Assert.Equal("Use Postgres", entry!.Title);
    }

    [Fact]
    public async Task CreateAsync_SplitsCsvFieldsIntoTrimmedLists()
    {
        var plugin = NewPlugin();
        var created = await plugin.CreateAsync("Title", "ctx", "decision", tags: "a, b ,c");
        var id = ExtractId(created);

        var entry = await _registry.GetByIdAsync(id);

        Assert.Equal(["a", "b", "c"], entry!.Tags);
    }

    // ── SupersedeAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SupersedeAsync_EmptyId_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.SupersedeAsync("", "ADR-0002");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task SupersedeAsync_EmptyNewId_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.SupersedeAsync("ADR-0001", "");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task SupersedeAsync_UnknownId_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        var result = await plugin.SupersedeAsync("ADR-9999", "ADR-0001");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task SupersedeAsync_Valid_MarksEntryAsSupersededAndNamesReplacement()
    {
        var plugin = NewPlugin();
        var id = ExtractId(await plugin.CreateAsync("Use MySQL", "ctx", "decision"));

        var result = await plugin.SupersedeAsync(id, "ADR-0099");

        Assert.StartsWith("[OK]", result);
        Assert.Contains("ADR-0099", result);
        var entry = await _registry.GetByIdAsync(id);
        Assert.Equal("Superseded", entry!.Status);
    }

    [Fact]
    public async Task SupersedeAsync_AlreadySuperseded_ReturnsInfoWithoutError()
    {
        var plugin = NewPlugin();
        var id = ExtractId(await plugin.CreateAsync("Use MySQL", "ctx", "decision"));
        await plugin.SupersedeAsync(id, "ADR-0099");

        var result = await plugin.SupersedeAsync(id, "ADR-0100");

        Assert.StartsWith("[INFO]", result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // CreateAsync's [OK] message is "[OK] Created ADR-0001: <title>" — pull the ID back out
    // rather than hardcoding IDs, since NextId() depends on creation order across tests.
    private static string ExtractId(string createResult) =>
        createResult.Split(' ')[2].TrimEnd(':');
}
