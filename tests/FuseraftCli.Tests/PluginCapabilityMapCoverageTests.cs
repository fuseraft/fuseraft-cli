using fuseraft.Infrastructure.Knowledge;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Infrastructure.Repository;

namespace FuseraftCli.Tests;

/// <summary>
/// Coverage test for <see cref="PluginCapabilityMap"/> — asserts every tool produced by each
/// of the plugins in the map's own "Capability vocabulary by plugin" doc list actually has a
/// capability entry. This is exactly the gap that let <c>git_rebase</c> and
/// <c>git_is_inside_work_tree</c> silently bypass capability filtering: an agent restricted to
/// <c>Capabilities: {"Git": ["read"]}</c> could still call them, because unmapped tools are
/// always-allowed by <see cref="PluginCapabilityMap.IsAllowed"/> — the right default for
/// MCP-registered tools, a silent security gap for a forgotten built-in one.
/// </summary>
public sealed class PluginCapabilityMapCoverageTests : IDisposable
{
    // Documented, intentional exceptions (see docs/design.md §12 and PluginCapabilityMap's own
    // doc comment) — tools deliberately left out of the map because they're low-risk enough to
    // always pass through regardless of declared Capabilities. Anything NOT in this set must
    // have a capability entry.
    private static readonly HashSet<string> IntentionallyUnmapped =
        new(StringComparer.OrdinalIgnoreCase) { "list_directory" };

    private readonly string _tempDir = Directory.CreateTempSubdirectory("fuseraft-cap-map-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    public static IEnumerable<object[]> CapabilityMappedPlugins()
    {
        // FileSystem is covered separately (FileSystemPlugin_EveryTool_HasACapabilityEntry)
        // since it's registered as two objects — see FileSystemManagementOps.
        yield return new object[] { "Shell",      new ShellPlugin() };
        yield return new object[] { "Git",        new GitPlugin() };
        yield return new object[] { "Http",        new HttpPlugin(new HttpClient()) };
        yield return new object[] { "Json",        new JsonPlugin() };
        yield return new object[] { "Document",    new DocumentPlugin() };
        yield return new object[] { "Search",      new SearchPlugin() };
        yield return new object[] { "Probe",       new ProbePlugin() };
        yield return new object[] { "CodeExecution", new CodeExecutionPlugin() };
    }

    [Theory]
    [MemberData(nameof(CapabilityMappedPlugins))]
    public void EveryToolFromCapabilityMappedPlugin_HasACapabilityEntry(string pluginName, object plugin) =>
        AssertAllCovered(pluginName, plugin);

    // Path-constructed plugins are covered separately since they need per-test temp storage
    // rather than the parameterless constructors above.

    [Fact]
    public void FileSystemPlugin_EveryTool_HasACapabilityEntry()
    {
        // FileSystem's tool surface spans two objects (see FileSystemManagementOps) —
        // both need to be passed so every reflected tool name is checked.
        var fsPlugin = new FileSystemPlugin();
        var fsOps    = new FileSystemManagementOps(fsPlugin);
        AssertAllCovered("FileSystem", fsPlugin, fsOps);
    }

    [Fact]
    public void ChangesPlugin_EveryTool_HasACapabilityEntry()
    {
        var plugin = new ChangesPlugin(Path.Combine(_tempDir, "changes.json"));
        AssertAllCovered("Changes", plugin);
    }

    [Fact]
    public void ScratchpadPlugin_EveryTool_HasACapabilityEntry()
    {
        var plugin = new ScratchpadPlugin("agent", _tempDir);
        AssertAllCovered("Scratchpad", plugin);
    }

    [Fact]
    public void ChatroomPlugin_EveryTool_HasACapabilityEntry()
    {
        var plugin = new ChatroomPlugin("agent", Path.Combine(_tempDir, "chatroom.jsonl"));
        AssertAllCovered("Chatroom", plugin);
    }

    [Fact]
    public void DecisionPlugin_EveryTool_HasACapabilityEntry()
    {
        var plugin = new DecisionPlugin(
            new AdrRegistry(new AdrStore(Path.Combine(_tempDir, "decisions"))),
            knowledgeLayer: null);
        AssertAllCovered("Decision", plugin);
    }

    [Fact]
    public void GraphPlugin_EveryTool_HasACapabilityEntry()
    {
        var plugin = new GraphPlugin(new RepositoryGraphStore(Path.Combine(_tempDir, "repository.graph")));
        AssertAllCovered("Graph", plugin);
    }

    private static void AssertAllCovered(string pluginName, params object[] plugins)
    {
        var functions = plugins.SelectMany(PluginRegistry.GetFunctionsFromObject).ToList();
        Assert.NotEmpty(functions);

        var uncovered = functions
            .Select(f => f.Name)
            .Where(name => !IntentionallyUnmapped.Contains(name) && !PluginCapabilityMap.HasCapabilityEntry(name))
            .ToList();

        Assert.True(uncovered.Count == 0,
            $"{pluginName} exposes tool(s) with no PluginCapabilityMap entry (silently unfiltered " +
            $"regardless of declared Capabilities): {string.Join(", ", uncovered)}");

        // GetPlugin is a second, independently-checkable field on the same map entry (added for
        // /tools restrict's reverse lookup) — a mismatch here means a tool was filed under the
        // wrong plugin name, which would make /tools restrict <this plugin> silently miss it
        // (or restrict the wrong plugin's tools) while IsAllowed-based filtering above still
        // passes, since IsAllowed never looks at the plugin field at all.
        var misfiled = functions
            .Select(f => f.Name)
            .Where(name => !IntentionallyUnmapped.Contains(name))
            .Where(name => !string.Equals(PluginCapabilityMap.GetPlugin(name), pluginName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(misfiled.Count == 0,
            $"{pluginName} exposes tool(s) whose PluginCapabilityMap.GetPlugin() doesn't match '{pluginName}': " +
            $"{string.Join(", ", misfiled)}");
    }
}
