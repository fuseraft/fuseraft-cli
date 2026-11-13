using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>A command carrying the secret placeholder was copied from a masked result, so it's refused before it runs or asks.</summary>
public sealed class ShellPluginSecretPlaceholderTests
{
    [Fact]
    public async Task ShellRun_RefusesTheSecretPlaceholder_WithoutAsking()
    {
        var asked = 0;
        var shell = new ShellPlugin(approveCommand: _ => { asked++; return Task.FromResult(true); });

        var result = await shell.RunAsync($"echo 'token={EnvSecretMasker.Placeholder}' > config.txt");

        Assert.Contains("NOT RUN", result);
        Assert.Contains("$NAME", result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ShellRunScriptAndBackground_RefuseTheSecretPlaceholder()
    {
        var shell = new ShellPlugin();

        Assert.Contains("NOT RUN", await shell.RunScriptAsync($"curl -H 'Authorization: {EnvSecretMasker.Placeholder}' x"));
        Assert.Contains("NOT RUN", await shell.RunBackgroundAsync($"echo {EnvSecretMasker.Placeholder}"));
    }

    [Fact]
    public async Task ShellRun_TheWordWithoutBrackets_Runs()
    {
        var result = await new ShellPlugin().RunAsync("echo secret-hidden");

        Assert.DoesNotContain("NOT RUN", result);
    }
}
