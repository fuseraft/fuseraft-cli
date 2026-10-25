using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

/// <summary>
/// Workspace scoping of the REPL memory store: a memory belongs to the workspace whose sessions
/// saved it. Isolates FUSERAFT_HOME, since the per-session refs files live under it — see
/// <see cref="FuseraftHomeEnvCollection"/>.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class MemoryStoreWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fuseraft_memws_{Guid.NewGuid():N}");
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly MemoryStore _store;
    private readonly string _projectA;
    private readonly string _projectB;

    public MemoryStoreWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, Path.Combine(_root, "home"));
        _store    = MemoryStore.CreateForTest(Path.Combine(_root, "home", "memory", "repl"));
        _projectA = Path.Combine(_root, "project-a");
        _projectB = Path.Combine(_root, "project-b");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SameNameInTwoWorkspaces_KeepsSeparateMemories()
    {
        await _store.SaveAsync(Entry("conventions", "A body"), _projectA, sessionId: "s1");
        await _store.SaveAsync(Entry("conventions", "B body"), _projectB, sessionId: "s2");

        var a = Assert.Single(await _store.LoadAllAsync(_projectA));
        var b = Assert.Single(await _store.LoadAllAsync(_projectB));
        Assert.Equal("A body", a.Body);
        Assert.Equal("B body", b.Body);
        Assert.NotEqual(a.Guid, b.Guid);
        Assert.Equal(2, (await _store.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task SameNameInSameWorkspace_UpdatesInPlace_AcrossSessions()
    {
        await _store.SaveAsync(Entry("conventions", "v1"), _projectA, sessionId: "s1");
        await _store.SaveAsync(Entry("conventions", "v2"), _projectA, sessionId: "s2");

        var entry = Assert.Single(await _store.LoadAllAsync(_projectA));
        Assert.Equal("v2", entry.Body);
        Assert.Single(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task Load_AfterThisSessionSaves_StillSeesOtherSessionsMemories()
    {
        // A project with a .fuseraft/ folder used to switch to loading only this session's refs.
        Directory.CreateDirectory(Path.Combine(_projectA, ".fuseraft"));
        await _store.SaveAsync(Entry("from_earlier_session", "old"), _projectA, sessionId: "s1");
        await _store.SaveAsync(Entry("from_this_session", "new"), _projectA, sessionId: "s2");

        var names = (await _store.LoadAllAsync(_projectA)).Select(e => e.Name).Order().ToList();

        Assert.Equal(["from_earlier_session", "from_this_session"], names);
    }

    [Fact]
    public async Task Save_WithoutSessionId_IsVisibleToWorkspaceLoad()
    {
        await _store.SaveAsync(Entry("no_session", "body"), _projectA);

        Assert.Single(await _store.LoadAllAsync(_projectA));
    }

    [Fact]
    public async Task Delete_InOneWorkspace_LeavesSameNamedMemoryOfOther()
    {
        await _store.SaveAsync(Entry("conventions", "A body"), _projectA, sessionId: "s1");
        await _store.SaveAsync(Entry("conventions", "B body"), _projectB, sessionId: "s2");

        Assert.True(await _store.DeleteAsync("conventions", _projectA));

        Assert.Empty(await _store.LoadAllAsync(_projectA));
        Assert.Equal("B body", Assert.Single(await _store.LoadAllAsync(_projectB)).Body);
        Assert.Equal("B body", Assert.Single(await _store.LoadAllAsync()).Body);
    }

    [Fact]
    public async Task Delete_RemovesRefsFromEverySessionOfTheWorkspace()
    {
        var guid = await _store.SaveAsync(Entry("shared", "v1"), _projectA, sessionId: "s1");
        await _store.SaveAsync(Entry("shared", "v2"), _projectA, sessionId: "s2");

        Assert.True(await _store.DeleteAsync("shared", _projectA));

        var sessionsDir = FuseraftPaths.GlobalProjectSessions(FuseraftPaths.ProjectSlug(_projectA));
        foreach (var refs in Directory.GetFiles(sessionsDir, "memory_refs.json", SearchOption.AllDirectories))
            Assert.DoesNotContain(guid, File.ReadAllText(refs));
    }

    private static MemoryEntry Entry(string name, string body) =>
        new() { Name = name, Description = $"{name} description", Type = "project", Body = body };
}
