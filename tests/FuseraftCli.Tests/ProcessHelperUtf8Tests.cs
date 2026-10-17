using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ProcessHelper.PreferUtf8"/>: on Windows, Python writing to a pipe used cp1252, so
/// printing → from a script died with UnicodeEncodeError mid-output. Child processes now get
/// PYTHONIOENCODING/PYTHONUTF8 (unless the user set them) and their output is decoded as UTF-8.
/// </summary>
public sealed class ProcessHelperUtf8Tests
{
    private static Task<ProcessResult> RunInShell(string command) =>
        OperatingSystem.IsWindows()
            ? ProcessHelper.RunAsync("cmd.exe", ["/c", command])
            : ProcessHelper.RunAsync("sh", ["-c", command]);

    // What the child should see: our default, or the user's own value when they set one.
    private static string Expected(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } own ? own : fallback;

    [Fact]
    public async Task RunAsync_ChildSeesPythonUtf8Settings()
    {
        var result = await RunInShell(OperatingSystem.IsWindows()
            ? "echo %PYTHONIOENCODING% %PYTHONUTF8%"
            : "echo \"$PYTHONIOENCODING $PYTHONUTF8\"");

        Assert.Equal($"{Expected("PYTHONIOENCODING", "utf-8")} {Expected("PYTHONUTF8", "1")}", result.Stdout.Trim());
    }

    [Fact]
    public void PreferUtf8_SetsUtf8OutputDecoding()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("x");

        ProcessHelper.PreferUtf8(startInfo);

        Assert.Equal("utf-8", startInfo.StandardOutputEncoding?.WebName);
        Assert.Equal("utf-8", startInfo.StandardErrorEncoding?.WebName);
    }

    [Fact]
    public async Task RunAsync_Utf8OutputFromChild_DecodesIntact()
    {
        if (OperatingSystem.IsWindows()) return; // printf with octal escapes is a POSIX sh feature

        var result = await RunInShell("printf '\\342\\206\\222\\n'");  // U+2192 → as UTF-8 bytes

        Assert.Equal("→", result.Stdout.Trim());
    }
}
