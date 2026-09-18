using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ShellEnvironment"/> — the shared shell-executable/flag resolution used
/// by both <see cref="ShellPlugin"/> and the REPL's <c>!&lt;command&gt;</c> escape. Both fields
/// are <c>static readonly</c>, resolved once at type load against the real OS, so these assert
/// the observed values rather than exercising <c>ResolveUnixShell</c>'s private fallback chain
/// in isolation (it takes no parameters and can't be redirected to a fake filesystem).
/// </summary>
public sealed class ShellEnvironmentTests
{
    [Fact]
    public void ShellFlag_MatchesCurrentOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? "/c" : "-c";
        Assert.Equal(expected, ShellEnvironment.ShellFlag);
    }

    [Fact]
    public void Shell_ResolvesToAnAbsolutePathOrKnownExecutableName()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd", ShellEnvironment.Shell);
        }
        else
        {
            // One of the bash candidates ResolveUnixShell checks, or its /bin/bash fallback —
            // never empty, and always an absolute path (ShellPlugin/ProcessHelper assume that).
            Assert.False(string.IsNullOrWhiteSpace(ShellEnvironment.Shell));
            Assert.True(Path.IsPathRooted(ShellEnvironment.Shell));
            Assert.EndsWith("bash", ShellEnvironment.Shell);
        }
    }
}
