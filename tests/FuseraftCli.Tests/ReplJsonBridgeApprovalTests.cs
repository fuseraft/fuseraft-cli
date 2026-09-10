using fuseraft.Cli;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the JSON-bridge side of /hitl shell-command approval in VS Code webview mode:
/// <see cref="ReplStdinPump.ExtractApproval"/> parsing stdin lines, and
/// <see cref="JsonBridgeHumanApprovalService.PromptShellCommandAsync"/> returning whatever the
/// pump relays. Fixes a bug where <c>ReplCommand.cs</c> always used
/// <see cref="ConsoleHumanApprovalService"/> even under <c>--vscode</c>, so its
/// <c>AnsiConsole</c>/<c>Console.ReadLine</c> prompt was invisible to the webview (non-JSON
/// stdout, and stdin only ever carries the extension's JSON messages) and every shell command
/// silently resolved as denied.
/// </summary>
public sealed class ReplJsonBridgeApprovalTests
{
    [Fact]
    public void ExtractApproval_Approved_ReturnsTrue() =>
        Assert.True(ReplStdinPump.ExtractApproval("""{"type":"approval_response","approved":true}"""));

    [Fact]
    public void ExtractApproval_Denied_ReturnsFalse() =>
        Assert.False(ReplStdinPump.ExtractApproval("""{"type":"approval_response","approved":false}"""));

    [Fact]
    public void ExtractApproval_WrongType_DeniesRatherThanMisreadsAsApproval() =>
        Assert.False(ReplStdinPump.ExtractApproval("""{"type":"user_input","text":"yes"}"""));

    [Fact]
    public void ExtractApproval_MalformedJson_DeniesRatherThanThrows() =>
        Assert.False(ReplStdinPump.ExtractApproval("not json at all"));

    [Fact]
    public void ExtractApproval_MissingApprovedField_Denies() =>
        Assert.False(ReplStdinPump.ExtractApproval("""{"type":"approval_response"}"""));

    [Fact]
    public async Task ReadApprovalResponseAsync_Eof_DeniesRatherThanThrows()
    {
        var pump = new ReplStdinPump(new StringReader(string.Empty), () => null);
        pump.Start();

        Assert.False(await pump.ReadApprovalResponseAsync());
    }

    [Fact]
    public async Task PromptShellCommandAsync_RelaysParsedApprovalFromStdin()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":true}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptShellCommandAsync("echo hi");

        Assert.True(allowed);
    }

    [Fact]
    public async Task PromptShellCommandAsync_DeniedResponse_ReturnsFalse()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":false}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptShellCommandAsync("rm -rf /");

        Assert.False(allowed);
    }

    [Fact]
    public async Task PromptToolActionAsync_RelaysParsedApprovalFromStdin()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":true}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptToolActionAsync("FileSystem", "write_file", "/tmp/scratch.txt");

        Assert.True(allowed);
    }

    [Fact]
    public async Task PromptToolActionAsync_DeniedResponse_ReturnsFalse()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":false}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptToolActionAsync("Git", "git_push", "origin main");

        Assert.False(allowed);
    }

    [Fact]
    public async Task PromptFileWriteAsync_RelaysParsedApprovalFromStdin()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":true}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptFileWriteAsync("write_file", "/tmp/scratch.txt", "old", "new");

        Assert.True(allowed);
    }

    [Fact]
    public async Task PromptFileWriteAsync_DeniedResponse_ReturnsFalse()
    {
        var pump = new ReplStdinPump(
            new StringReader("""{"type":"approval_response","approved":false}""" + "\n"), () => null);
        pump.Start();
        var service = new JsonBridgeHumanApprovalService(pump);

        var allowed = await service.PromptFileWriteAsync("patch_file", "/tmp/scratch.txt", "old", "new");

        Assert.False(allowed);
    }

    // Regression coverage for the actual "Stop button doesn't work" bug: on Windows there's no
    // way to deliver a real SIGINT to a child process, so the extension sends the interrupt as an
    // in-band {"type":"interrupt"} stdin line instead. The old design only read stdin from inside
    // the main turn loop once per turn boundary, so an interrupt line arriving mid-turn just sat
    // unread until the turn finished on its own. These verify the pump acts on it immediately,
    // independently of whatever ReadInputAsync/ReadApprovalResponseAsync are doing.
    [Fact]
    public void IsInterruptLine_RecognisesInterruptMessage() =>
        Assert.True(ReplStdinPump.IsInterruptLine("""{"type":"interrupt"}"""));

    [Fact]
    public void IsInterruptLine_IgnoresOtherMessageTypes() =>
        Assert.False(ReplStdinPump.IsInterruptLine("""{"type":"user_input","text":"hello"}"""));

    [Fact]
    public async Task Pump_CancelsActiveRequest_WhenInterruptArrives()
    {
        using var cts = new CancellationTokenSource();
        var pump = new ReplStdinPump(new StringReader("""{"type":"interrupt"}""" + "\n"), () => cts);
        pump.Start();

        // Poll rather than sleep a fixed amount — the pump races the test on a background task.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Pump_InterruptDoesNotBlockBehindQueuedInputLine()
    {
        // The interrupt line comes FIRST, followed by a normal turn line. If the pump only
        // forwarded raw lines to a single consumer in arrival order (rather than acting on
        // interrupts immediately, out of band), a caller awaiting ReadInputAsync could still
        // observe the right eventual result — so the real assertion is that the interrupt lands
        // without ever having to be read via ReadInputAsync first.
        using var cts = new CancellationTokenSource();
        var input = string.Join('\n',
            """{"type":"interrupt"}""",
            """{"type":"user_input","text":"hello"}""") + "\n";
        var pump = new ReplStdinPump(new StringReader(input), () => cts);
        pump.Start();

        var text = await pump.ReadInputAsync();

        Assert.Equal("hello", text);
        Assert.True(cts.IsCancellationRequested);
    }
}
