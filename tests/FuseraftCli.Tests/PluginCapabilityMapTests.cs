using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="PluginCapabilityMap.IsAllowed"/> is the enforcement point every per-agent
/// <c>Capabilities</c> restriction relies on — including the read-only locks applied to the
/// recon/review/verify-only agents across the init templates (ArtifactPlugin instances like
/// Conventions/DiscoveryBrief/Preflight/AuditFindings, and the plain FileSystem:[read] locks
/// on Reviewer/Verifier/Executor agents).
/// Despite that, it had no direct unit coverage. These tests close that gap at the one place
/// all of those fixes ultimately depend on, instead of re-proving the same already-verified
/// wiring with another live model run per agent.
/// </summary>
public sealed class PluginCapabilityMapTests
{
    [Theory]
    [InlineData("read_file")]
    [InlineData("grep_file")]
    [InlineData("get_file_summary")]
    [InlineData("get_file_info")]
    [InlineData("list_files")]
    public void FileSystemReadTool_Allowed_WhenOnlyReadGranted(string tool)
    {
        Assert.True(PluginCapabilityMap.IsAllowed(tool, ["read"]));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("patch_file")]
    [InlineData("create_directory")]
    [InlineData("copy_file")]
    [InlineData("move_file")]
    [InlineData("set_permissions")]
    [InlineData("save_file_summary")]
    public void FileSystemWriteTool_Denied_WhenOnlyReadGranted(string tool)
    {
        Assert.False(PluginCapabilityMap.IsAllowed(tool, ["read"]));
    }

    [Theory]
    [InlineData("delete_file")]
    [InlineData("delete_directory")]
    public void FileSystemDeleteTool_Denied_WhenOnlyReadGranted(string tool)
    {
        Assert.False(PluginCapabilityMap.IsAllowed(tool, ["read"]));
    }

    [Fact]
    public void WriteTool_Allowed_WhenWriteGranted()
    {
        Assert.True(PluginCapabilityMap.IsAllowed("write_file", ["write"]));
    }

    [Fact]
    public void UnknownTool_AlwaysAllowed_RegardlessOfCapabilities()
    {
        // Tools absent from the map (custom plugin methods, MCP tools, future built-ins)
        // must never be silently blocked by a capability filter that hasn't been updated —
        // this is what lets write_file_audit_findings/write_file_preflight/write_file_conventions
        // stay reachable on an agent locked to FileSystem:[read].
        Assert.True(PluginCapabilityMap.IsAllowed("write_file_audit_findings", ["read"]));
        Assert.True(PluginCapabilityMap.IsAllowed("write_file_preflight", ["read"]));
        Assert.True(PluginCapabilityMap.IsAllowed("write_file_conventions", []));
    }

    [Fact]
    public void EmptyCapabilityList_DeniesEveryMappedTool()
    {
        Assert.False(PluginCapabilityMap.IsAllowed("read_file", []));
        Assert.False(PluginCapabilityMap.IsAllowed("write_file", []));
    }

    [Fact]
    public void CapabilityMatch_IsCaseInsensitive()
    {
        Assert.True(PluginCapabilityMap.IsAllowed("read_file", ["READ"]));
        Assert.True(PluginCapabilityMap.IsAllowed("READ_FILE", ["read"]));
    }

    [Theory]
    [InlineData("shell_run", "run")]
    [InlineData("shell_get_env", "read")]
    [InlineData("git_commit", "write")]
    [InlineData("git_status", "read")]
    public void NonFileSystemPlugins_MapToExpectedTags(string tool, string requiredTag)
    {
        Assert.True(PluginCapabilityMap.IsAllowed(tool, [requiredTag]));
        Assert.False(PluginCapabilityMap.IsAllowed(tool, ["some-other-tag"]));
    }

    // GetPlugin — the reverse lookup the REPL's /tools restrict command relies on to find every
    // tool belonging to a given plugin regardless of which REPL tool-category bucket holds it.

    [Theory]
    [InlineData("read_file", "FileSystem")]
    [InlineData("delete_directory", "FileSystem")]
    [InlineData("shell_run", "Shell")]
    [InlineData("shell_run_background", "Shell")]
    [InlineData("git_push", "Git")]
    [InlineData("git_status", "Git")]
    [InlineData("http_post", "Http")]
    public void GetPlugin_ReturnsOwningPlugin(string tool, string expectedPlugin) =>
        Assert.Equal(expectedPlugin, PluginCapabilityMap.GetPlugin(tool), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void GetPlugin_ReturnsNull_ForUnmappedTool()
    {
        Assert.Null(PluginCapabilityMap.GetPlugin("write_file_audit_findings"));
        Assert.Null(PluginCapabilityMap.GetPlugin("some_mcp_tool"));
    }

    [Theory]
    [InlineData("FileSystem")]
    [InlineData("Shell")]
    [InlineData("Git")]
    [InlineData("Http")]
    public void KnownPlugins_ContainsCoreRestrictablePlugins(string plugin) =>
        Assert.Contains(plugin, PluginCapabilityMap.KnownPlugins);

    [Theory]
    [InlineData("Todo")]
    [InlineData("SubAgent")]
    [InlineData("SessionContext")]
    public void KnownPlugins_ExcludesPluginsWithNoCapabilityTags(string plugin) =>
        Assert.DoesNotContain(plugin, PluginCapabilityMap.KnownPlugins);
}
