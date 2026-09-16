using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="DefaultSecurityPolicy"/> — the single source of truth for the
/// ".env is always denied" baseline shared by ReplCommand.cs (REPL) and PluginRegistry.Configure
/// / AgentToolResolver (orchestration). See FileSystemPluginTests and PluginRegistryTests for
/// proof that the merged result actually blocks a real read_file call in each surface.
/// </summary>
public sealed class DefaultSecurityPolicyTests
{
    [Fact]
    public void MergeFileSystemDeny_NullConfig_ReturnsBaselinePatternsOnly()
    {
        var result = DefaultSecurityPolicy.MergeFileSystemDeny(null);

        Assert.Contains(".env", result);
        Assert.Contains(".env.*", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeFileSystemDeny_ConfiguredDeny_MergesWithBaseline()
    {
        var configured = new FileSystemPermissions { Deny = ["secrets/**"] };

        var result = DefaultSecurityPolicy.MergeFileSystemDeny(configured);

        Assert.Contains(".env", result);
        Assert.Contains(".env.*", result);
        Assert.Contains("secrets/**", result);
    }

    [Fact]
    public void MergeFileSystemDeny_ConfiguredDenyDuplicatesBaseline_NotDuplicated()
    {
        // Case-insensitive dedup — a project explicitly listing ".ENV" shouldn't produce two
        // near-identical glob entries.
        var configured = new FileSystemPermissions { Deny = [".ENV"] };

        var result = DefaultSecurityPolicy.MergeFileSystemDeny(configured);

        Assert.Single(result, p => string.Equals(p, ".env", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergeShellPolicy_NullConfig_ReturnsPolicyWithBaselineDenyOnly()
    {
        var result = DefaultSecurityPolicy.MergeShellPolicy(null);

        Assert.Contains(".env", result.Deny);
        Assert.Empty(result.Allow);
    }

    [Fact]
    public void MergeShellPolicy_ConfiguredPolicy_PreservesAllowAndMergesDeny()
    {
        var configured = new ShellPolicy { Allow = ["go test"], Deny = ["rm -rf"] };

        var result = DefaultSecurityPolicy.MergeShellPolicy(configured);

        Assert.Contains("go test", result.Allow);
        Assert.Contains("rm -rf", result.Deny);
        Assert.Contains(".env", result.Deny);
    }
}
