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

    // Nested paths and credential files, through a live registry ---------------------------------

    private FileSystemPlugin ConfiguredFs(SecurityConfig config)
    {
        var registry = new PluginRegistry().RegisterDefaults().Configure(config);
        Assert.True(registry.TryGet("FileSystem", out var fsObj));
        return Assert.IsType<FileSystemPlugin>(fsObj);
    }

    private async Task WriteAsync(string relative, string content = "TOP-SECRET-VALUE")
    {
        var full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    [Theory]
    [InlineData("backend/.env")]
    [InlineData("apps/web/.env.local")]
    [InlineData("a/b/c/d/.env.production")]
    public async Task Configure_NestedEnvFiles_AreDenied_NotJustTheOneAtTheRoot(string relative)
    {
        await WriteAsync(relative);
        var fs = ConfiguredFs(new SecurityConfig { FileSystemSandboxPath = _dir });

        var result = await fs.ReadFileAsync(Path.Combine(_dir, relative));

        Assert.StartsWith("[DENIED]", result);
        Assert.DoesNotContain("TOP-SECRET-VALUE", result);
    }

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("deploy/keys/id_ed25519")]
    [InlineData("home/user/.aws/credentials")]
    [InlineData(".netrc")]
    [InlineData("tools/.pgpass")]
    [InlineData("x/.git-credentials")]
    public async Task Configure_CredentialFiles_AreDeniedAtAnyDepth(string relative)
    {
        await WriteAsync(relative);
        var fs = ConfiguredFs(new SecurityConfig { FileSystemSandboxPath = _dir });

        var result = await fs.ReadFileAsync(Path.Combine(_dir, relative));

        Assert.StartsWith("[DENIED]", result);
        Assert.DoesNotContain("TOP-SECRET-VALUE", result);
    }

    [Theory]
    [InlineData("id_rsa.pub")]
    [InlineData("keys/id_ed25519.pub")]
    [InlineData("config.yaml")]
    [InlineData("src/credentials.md")]
    [InlineData(".aws/config")]
    [InlineData(".npmrc")]
    public async Task Configure_PublicKeysAndOrdinaryFiles_StayReadable(string relative)
    {
        await WriteAsync(relative, "harmless content");
        var fs = ConfiguredFs(new SecurityConfig { FileSystemSandboxPath = _dir });

        var result = await fs.ReadFileAsync(Path.Combine(_dir, relative));

        Assert.DoesNotContain("[DENIED]", result);
        Assert.Contains("harmless content", result);
    }

    [Fact]
    public async Task Configure_OptedOutOfCredentialFiles_ReadsThemButStillDeniesEnv()
    {
        await WriteAsync("id_rsa");
        await WriteAsync("sub/.env");
        var fs = ConfiguredFs(new SecurityConfig { FileSystemSandboxPath = _dir, DenyCredentialFiles = false });

        var key = await fs.ReadFileAsync(Path.Combine(_dir, "id_rsa"));
        var env = await fs.ReadFileAsync(Path.Combine(_dir, "sub", ".env"));

        Assert.DoesNotContain("[DENIED]", key);
        Assert.StartsWith("[DENIED]", env);
    }

    [Fact]
    public async Task Configure_NoSandbox_StillDeniesDirectoryQualifiedCredentialPaths()
    {
        // Without a sandbox root the matcher used to see only the file name, so a pattern naming a
        // directory (**/.aws/credentials) could never match — the --yolo REPL case.
        await WriteAsync("home/user/.aws/credentials");
        await WriteAsync("proj/backend/.env");
        var fs = ConfiguredFs(new SecurityConfig());

        var creds = await fs.ReadFileAsync(Path.Combine(_dir, "home", "user", ".aws", "credentials"));
        var env   = await fs.ReadFileAsync(Path.Combine(_dir, "proj", "backend", ".env"));

        Assert.StartsWith("[DENIED]", creds);
        Assert.StartsWith("[DENIED]", env);
    }

    [Fact]
    public async Task Configure_NoSandbox_BareUserPatternsStillMatchByFileName()
    {
        // Backward compatibility for the unsandboxed matching change: a user's non-recursive
        // pattern matched by file name anywhere before, and must keep doing so.
        await WriteAsync("deep/er/secret.txt");
        var fs = ConfiguredFs(new SecurityConfig
        {
            FileSystemPermissions = new FileSystemPermissions { Deny = ["secret.txt"] },
        });

        var result = await fs.ReadFileAsync(Path.Combine(_dir, "deep", "er", "secret.txt"));

        Assert.StartsWith("[DENIED]", result);
    }

    // Shell — a credentials file named in a command (paths are nonexistent, so a regression is harmless)

    [Theory]
    [InlineData("cat /tmp/fuseraft-test-nonexistent/id_rsa")]
    [InlineData("cat ~/.ssh/id_ed25519")]
    [InlineData("base64 /tmp/fuseraft-test-nonexistent/.aws/credentials")]
    [InlineData("cp ~/.netrc /tmp/fuseraft-test-nonexistent/")]
    [InlineData("cat /tmp/fuseraft-test-nonexistent/.pgpass")]
    [InlineData("tar cf x.tar /tmp/fuseraft-test-nonexistent/.git-credentials")]
    [InlineData("cat ~/.ssh/id_*")]
    public async Task Configure_ShellCommandNamingACredentialsFile_IsDenied(string command)
    {
        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { FileSystemSandboxPath = _dir });
        Assert.True(registry.TryGet("Shell", out var shellObj));
        var shell = Assert.IsType<ShellPlugin>(shellObj);

        var result = await shell.RunAsync(command, _dir);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("credentials file", result);
    }

    [Fact]
    public async Task Configure_ShellOptedOutOfCredentialFiles_NoLongerDeniesThem()
    {
        var registry = new PluginRegistry().RegisterDefaults()
            .Configure(new SecurityConfig { FileSystemSandboxPath = _dir, DenyCredentialFiles = false });
        Assert.True(registry.TryGet("Shell", out var shellObj));
        var shell = Assert.IsType<ShellPlugin>(shellObj);

        var result = await shell.RunAsync("cat /tmp/fuseraft-test-nonexistent/id_rsa", _dir);

        Assert.DoesNotContain("credentials file", result);
    }
}
