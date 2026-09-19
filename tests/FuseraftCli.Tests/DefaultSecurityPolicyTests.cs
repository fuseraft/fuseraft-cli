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

        Assert.Contains("**/.env", result);
        Assert.Contains("**/.env.*", result);
        Assert.Equal(DefaultSecurityPolicy.SecretFileGlobs.Count + DefaultSecurityPolicy.CredentialFileGlobs.Count, result.Count);
    }

    [Fact]
    public void MergeFileSystemDeny_Baseline_MatchesAtAnyDepth_NotJustTheSandboxRoot()
    {
        // The bug this pins: a bare ".env" glob only matches "<root>/.env", so backend/.env and
        // apps/web/.env.local were readable. Every baseline glob must be recursive.
        Assert.All(DefaultSecurityPolicy.SecretFileGlobs.Concat(DefaultSecurityPolicy.CredentialFileGlobs),
            g => Assert.StartsWith("**/", g));
    }

    [Fact]
    public void MergeFileSystemDeny_CredentialFiles_CoverThePlainTextSecretStores()
    {
        var result = DefaultSecurityPolicy.MergeFileSystemDeny(null);

        foreach (var expected in new[]
                 {
                     "**/id_rsa", "**/id_dsa", "**/id_ecdsa", "**/id_ed25519",
                     "**/.aws/credentials", "**/.netrc", "**/_netrc", "**/.pgpass", "**/.git-credentials",
                 })
            Assert.Contains(expected, result);
    }

    [Fact]
    public void MergeFileSystemDeny_CredentialFiles_DoNotCoverPublicKeysOrMixedConfigFiles()
    {
        var result = DefaultSecurityPolicy.MergeFileSystemDeny(null);

        Assert.DoesNotContain(result, p => p.Contains(".pub", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, p => p.Contains(".npmrc") || p.Contains(".pem") || p.Contains("docker"));
    }

    [Fact]
    public void MergeFileSystemDeny_OptedOut_DropsCredentialFilesButKeepsEnv()
    {
        var result = DefaultSecurityPolicy.MergeFileSystemDeny(null, denyCredentialFiles: false);

        Assert.Equal(["**/.env", "**/.env.*"], result);
    }

    [Fact]
    public void SecurityConfig_DenyCredentialFiles_DefaultsToTrue() =>
        Assert.True(new SecurityConfig().DenyCredentialFiles);

    [Fact]
    public void MergeFileSystemDeny_ConfiguredDeny_MergesWithBaseline()
    {
        var configured = new FileSystemPermissions { Deny = ["secrets/**"] };

        var result = DefaultSecurityPolicy.MergeFileSystemDeny(configured);

        Assert.Contains("**/.env", result);
        Assert.Contains("**/.env.*", result);
        Assert.Contains("secrets/**", result);
    }

    [Fact]
    public void MergeFileSystemDeny_ConfiguredDenyDuplicatesBaseline_NotDuplicated()
    {
        // Case-insensitive dedup — a project explicitly listing ".ENV" shouldn't produce two
        // near-identical glob entries.
        var configured = new FileSystemPermissions { Deny = ["**/.ENV"] };

        var result = DefaultSecurityPolicy.MergeFileSystemDeny(configured);

        Assert.Single(result, p => string.Equals(p, "**/.env", StringComparison.OrdinalIgnoreCase));
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
