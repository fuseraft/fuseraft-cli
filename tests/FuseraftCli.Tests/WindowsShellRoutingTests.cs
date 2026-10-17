using System.Text;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="WindowsShellRouting"/>: which commands go to PowerShell, the script an explicit
/// <c>powershell -Command</c> carries once its command-line escaping is undone, and the
/// <c>-EncodedCommand</c> invocation that runs it with no quoting layer in between.
/// </summary>
public sealed class WindowsShellRoutingTests
{
    [Theory]
    [InlineData(@"a b  c", new[] { "a", "b", "c" })]
    [InlineData(@"""a b"" c", new[] { "a b", "c" })]
    [InlineData(@"""say \""hi\"""" x", new[] { @"say ""hi""", "x" })]
    [InlineData(@"""C:\dir\"" x", new[] { @"C:\dir"" x" })]
    [InlineData(@"""C:\dir\\"" x", new[] { @"C:\dir\", "x" })]
    [InlineData(@"C:\a\b ""in """"quotes""""""", new[] { @"C:\a\b", @"in ""quotes""" })]
    [InlineData(@"""""", new[] { "" })]
    public void SplitCommandLine_FollowsTheCRuntimeRules(string commandLine, string[] expected) =>
        Assert.Equal(expected, WindowsShellRouting.SplitCommandLine(commandLine));

    [Theory]
    [InlineData(@"powershell -NoProfile -Command ""Write-Host \""hi\""""", @"Write-Host ""hi""")]
    [InlineData(@"powershell -ExecutionPolicy Bypass -Command ""Get-Date""", "Get-Date")]
    [InlineData(@"pwsh -c ""Get-Date | Out-String""", "Get-Date | Out-String")]
    [InlineData(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoProfile -Command ""Get-Date""", "Get-Date")]
    [InlineData(@"powershell.exe -Command Get-ChildItem -Recurse", "Get-ChildItem -Recurse")]
    [InlineData(@"powershell -Command 'Get-Date'", "Get-Date")]
    [InlineData(@"powershell -Command ""'a' + 'b'""", "'a' + 'b'")]
    public void ExtractsTheScriptPowerShellWouldRun(string command, string expected)
    {
        Assert.True(WindowsShellRouting.TryExtractPowerShellScript(command, out var script));
        Assert.Equal(expected, script);
    }

    [Fact]
    public void ExtractsAConnectionStringIntact()
    {
        var command = @"powershell -NoProfile -Command ""$cs = \""Data Source=sqlhost\inst;Initial Catalog=App;Integrated Security=True\""; " +
                      @"$c = New-Object System.Data.SqlClient.SqlConnection $cs; $c.ConnectionString""";

        Assert.True(WindowsShellRouting.TryExtractPowerShellScript(command, out var script));
        Assert.Equal(
            @"$cs = ""Data Source=sqlhost\inst;Initial Catalog=App;Integrated Security=True""; " +
            @"$c = New-Object System.Data.SqlClient.SqlConnection $cs; $c.ConnectionString",
            script);
    }

    [Theory]
    [InlineData(@"powershell -File build.ps1")]
    [InlineData(@"powershell -EncodedCommand ZQBjAGgAbwA=")]
    [InlineData(@"powershell -Command -")]
    [InlineData(@"powershell ""Get-Date""")]
    [InlineData(@"echo powershell -Command Get-Date")]
    [InlineData(@"cd src && powershell -Command ""Get-Date""")]
    [InlineData(@"powershellish -Command x")]
    public void LeavesOtherFormsToCmd(string command) =>
        Assert.False(WindowsShellRouting.TryExtractPowerShellScript(command, out _));

    [Theory]
    [InlineData("Get-ChildItem -Recurse -Filter *.cs")]
    [InlineData("dir | Select-String TODO")]
    [InlineData("mkdir out && Get-ChildItem out")]
    [InlineData("echo $env:USERPROFILE")]
    [InlineData(@"type ""$env:TEMP\log.txt""")]
    [InlineData("[IO.File]::ReadAllText('a.txt')")]
    [InlineData(@"& ""C:\tools\sqlcmd.exe"" -Q ""SELECT 1""")]
    [InlineData("if ($LASTEXITCODE -ne 0) { exit 1 }")]
    public void RecognizesPowerShellSyntax(string command) =>
        Assert.True(WindowsShellRouting.LooksLikePowerShell(command));

    [Theory]
    [InlineData("dotnet build -c Release")]
    [InlineData(@"git commit -m ""fix Get-Item handling; Set-Content too""")]
    [InlineData(@"dir /s /b *.cs")]
    [InlineData("echo %USERPROFILE%")]
    [InlineData(@"findstr /i ""Set-Content"" notes.txt")]
    [InlineData("type a.txt | findstr error")]
    [InlineData("msbuild App.sln /p:Configuration=Release && echo done")]
    [InlineData("start-dev.cmd --watch")]
    [InlineData("build && test-runner.exe --all")]
    public void LeavesCmdSyntaxAlone(string command) =>
        Assert.False(WindowsShellRouting.LooksLikePowerShell(command));

    [Fact]
    public void EncodedCommand_CarriesTheScriptVerbatim()
    {
        var script = @"Write-Output ""a 'b' \""c\"" | & > %PATH%""";

        var args = WindowsShellRouting.EncodedCommandArgs(script)!;

        Assert.Equal("-EncodedCommand", args[^2]);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
        Assert.EndsWith("\n" + script, decoded);
        Assert.StartsWith("$ProgressPreference = 'SilentlyContinue'", decoded);
    }

    [Fact]
    public void EncodedCommand_TooLongForACommandLine_IsNull() =>
        Assert.Null(WindowsShellRouting.EncodedCommandArgs(new string('x', 12_000)));

    [Fact]
    public void ScriptFile_IsWrittenWithAByteOrderMark() =>
        Assert.NotEmpty(WindowsShellRouting.ScriptFile("Get-Date").Encoding.GetPreamble());

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("auto", null)]
    [InlineData("cmd", "Cmd")]
    [InlineData("PowerShell", "PowerShell")]
    [InlineData("pwsh", "PowerShell")]
    public void ParsesTheShellArgument(string? shell, string? expected)
    {
        Assert.Null(WindowsShellRouting.ParseShellArg(shell, out var choice));
        Assert.Equal(expected, choice?.ToString());
    }

    [Fact]
    public void RejectsAnUnknownShell() =>
        Assert.Contains("Unknown shell 'bash'", WindowsShellRouting.ParseShellArg("bash", out _));

    [Fact]
    public async Task ShellRun_NamingAWindowsShellOffWindows_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        var shell = new ShellPlugin();

        Assert.Contains("only available on Windows", await shell.RunAsync("Get-Date", shell: "powershell"));
        Assert.Contains("Unknown shell", await shell.RunAsync("echo hi", shell: "bash"));
    }

    // Windows only — these start real PowerShell. Elsewhere they return immediately.

    [Fact]
    public async Task OnWindows_PowerShellGetsTheCommandAsWritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellPlugin();

        var result = await shell.RunAsync(
            @"$cs = ""Data Source=.\sql;Initial Catalog='App'""; Write-Output ""[$cs] | & > %PATH%""", shell: "powershell");

        Assert.Contains(@"[Data Source=.\sql;Initial Catalog='App'] | & > %PATH%", result);
    }

    [Fact]
    public async Task OnWindows_PlainPowerShellIsRoutedWithoutAShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellPlugin();

        Assert.Contains("routed-ok", await shell.RunAsync(@"Write-Output ""routed-$('ok')"""));
        Assert.Contains("routed-ok", await shell.RunAsync(@"powershell -NoProfile -Command ""Write-Output \""routed-$('ok')\"""""));
    }
}
