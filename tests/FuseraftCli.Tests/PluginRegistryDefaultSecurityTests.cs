using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Proves DefaultSecurityPolicy's baseline actually reaches a live orchestration session, not
/// just that the merge helper returns the right list (see DefaultSecurityPolicyTests) — the
/// gap this closes: before this, FileSystemPermissions.Deny only applied via
/// SandboxEnforcementFilter, which requires an explicit FileSystemSandboxPath and an explicit
/// Deny entry. Configure() must protect .env with a bare `new SecurityConfig()` — no sandbox
/// path, no Deny entry, nothing configured at all.
/// </summary>
public sealed class PluginRegistryDefaultSecurityTests : IDisposable
{
    private readonly string _dir;

    public PluginRegistryDefaultSecurityTests()
    {
        _dir = Directory.CreateTempSubdirectory("fuseraft_pluginreg_sec_").FullName;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Configure_BareSecurityConfig_StillDeniesEnvFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, ".env"), "BOOMI_API_TOKEN=super-secret");

        var registry = new PluginRegistry().RegisterDefaults()
            .Configure(new SecurityConfig { FileSystemSandboxPath = _dir });

        Assert.True(registry.TryGet("FileSystem", out var fsObj));
        var fs = Assert.IsType<FileSystemPlugin>(fsObj);

        var result = await fs.ReadFileAsync(Path.Combine(_dir, ".env"));

        Assert.StartsWith("[DENIED]", result);
        Assert.DoesNotContain("super-secret", result);
    }

    [Fact]
    public async Task Configure_ConfiguredDenyPreserved_AlongsideDefault()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "secrets"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "secrets", "key.pem"), "-----BEGIN KEY-----");

        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig
        {
            FileSystemSandboxPath = _dir,
            FileSystemPermissions = new FileSystemPermissions { Deny = ["secrets/**"] },
        });

        Assert.True(registry.TryGet("FileSystem", out var fsObj));
        var fs = Assert.IsType<FileSystemPlugin>(fsObj);

        var pemResult = await fs.ReadFileAsync(Path.Combine(_dir, "secrets", "key.pem"));
        Assert.StartsWith("[DENIED]", pemResult);

        // .env still denied even though only "secrets/**" was explicitly configured.
        await File.WriteAllTextAsync(Path.Combine(_dir, ".env"), "X=1");
        var envResult = await fs.ReadFileAsync(Path.Combine(_dir, ".env"));
        Assert.StartsWith("[DENIED]", envResult);
    }

    [Fact]
    public async Task Configure_BareSecurityConfig_ShellCommandReferencingEnvDenied()
    {
        var registry = new PluginRegistry().RegisterDefaults()
            .Configure(new SecurityConfig { FileSystemSandboxPath = _dir });

        Assert.True(registry.TryGet("Shell", out var shellObj));
        var shell = Assert.IsType<ShellPlugin>(shellObj);

        var result = await shell.RunAsync("cat .env", _dir);

        Assert.StartsWith("[DENIED]", result);
    }
}
