using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tool results show a secret's value as the placeholder, so write_file and patch_file refuse to write the placeholder
/// over a file that holds the real value, and say when an edit puts a secret's value into a file.
/// </summary>
public sealed class FileSystemPluginSecretTests : IDisposable
{
    private const string Name  = "FUSERAFT_TEST_FSP_SECRET_TOKEN";
    private const string Value = "fsp-test-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft_fsp_secret_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileSystemPlugin _plugin;

    public FileSystemPluginSecretTests()
    {
        Directory.CreateDirectory(_dir);
        _plugin = new FileSystemPlugin(sandboxRoot: _dir);
        Environment.SetEnvironmentVariable(Name, Value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Name, null);
        Directory.Delete(_dir, recursive: true);
    }

    private string PathOf(string file) => Path.Combine(_dir, file);

    [Fact]
    public async Task WriteFile_PlaceholderOverAFileHoldingTheSecret_IsRefused_AndLeavesIt()
    {
        File.WriteAllText(PathOf("config.txt"), $"token={Value}\nlevel=info\n");

        var result = await _plugin.WriteFileAsync(PathOf("config.txt"), $"token={EnvSecretMasker.Placeholder}\nlevel=debug\n");

        Assert.Contains("would replace the real value", result);
        Assert.Contains(Value, File.ReadAllText(PathOf("config.txt")));
    }

    [Fact]
    public async Task WriteFile_PlaceholderInAFileWithoutSecrets_IsWritten()
    {
        var result = await _plugin.WriteFileAsync(PathOf("docs.md"), $"Secrets show as {EnvSecretMasker.Placeholder}.\n");

        Assert.DoesNotContain("[ERROR]", result);
        Assert.Contains(EnvSecretMasker.Placeholder, File.ReadAllText(PathOf("docs.md")));
    }

    [Fact]
    public async Task PatchFile_PlaceholderInOldText_IsRefusedWithTheReason()
    {
        File.WriteAllText(PathOf("config.txt"), $"token={Value}\nlevel=info\n");

        var result = await _plugin.PatchFileAsync(PathOf("config.txt"), $"token={EnvSecretMasker.Placeholder}\nlevel=info", "level=debug");

        Assert.Contains("would replace the real value", result);
    }

    [Fact]
    public async Task PatchFile_AroundTheSecret_StillWorks()
    {
        File.WriteAllText(PathOf("config.txt"), $"token={Value}\nlevel=info\n");

        var result = await _plugin.PatchFileAsync(PathOf("config.txt"), "level=info", "level=debug");

        Assert.DoesNotContain("[ERROR]", result);
        Assert.Equal($"token={Value}\nlevel=debug\n", File.ReadAllText(PathOf("config.txt")));
    }

    [Fact]
    public async Task WriteFile_PuttingASecretIntoAFile_WarnsByName()
    {
        var result = await _plugin.WriteFileAsync(PathOf("leak.txt"), $"dump: {Value}\n");

        Assert.Contains($"value of {Name}", result);
        Assert.DoesNotContain(Value, result);
    }

    [Fact]
    public async Task PatchFile_NotAddingASecret_DoesNotWarn()
    {
        File.WriteAllText(PathOf("config.txt"), $"token={Value}\nlevel=info\n");

        var result = await _plugin.PatchFileAsync(PathOf("config.txt"), "level=info", "level=debug");

        Assert.DoesNotContain("Warning", result);
    }
}
