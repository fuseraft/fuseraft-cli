using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// A command that is a context-compaction placeholder — an earlier call's arguments as they read
/// after compaction — is refused before it reaches the shell or the approval prompt.
/// </summary>
public sealed class ShellPluginElisionPlaceholderTests
{
    [Fact]
    public async Task ShellRun_RefusesAnElidedCommand_WithoutAsking()
    {
        var asked = 0;
        var shell = new ShellPlugin(approveCommand: _ => { asked++; return Task.FromResult(true); });

        var result = await shell.RunAsync(ElisionMarkers.ArgValueNote(1_234));

        Assert.Contains("NOT RUN", result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ShellRunScript_RefusesAnElidedScript()
    {
        var shell = new ShellPlugin();

        Assert.Contains("NOT RUN", await shell.RunScriptAsync(ElisionMarkers.ArgValueNote(5_000)));
    }

    [Fact]
    public async Task ShellRun_StillRunsACommandThatMerelyMentionsElided()
    {
        var shell = new ShellPlugin();

        var result = await shell.RunAsync("echo ELIDED");

        Assert.DoesNotContain("NOT RUN", result);
        Assert.Contains("ELIDED", result);
    }
}
