using fuseraft.Cli;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the JSON-bridge side of /hitl shell-command approval in VS Code webview mode:
/// <see cref="ReplJsonBridge.ReadApprovalResponse"/> parsing stdin, and
/// <see cref="JsonBridgeHumanApprovalService.PromptShellCommandAsync"/> returning whatever that
/// parse produced. Fixes a bug where <c>ReplCommand.cs</c> always used
/// <see cref="ConsoleHumanApprovalService"/> even under <c>--vscode</c>, so its
/// <c>AnsiConsole</c>/<c>Console.ReadLine</c> prompt was invisible to the webview (non-JSON
/// stdout, and stdin only ever carries the extension's JSON messages) and every shell command
/// silently resolved as denied.
/// </summary>
public sealed class ReplJsonBridgeApprovalTests
{
    private static bool ReadApprovalResponseWithStdin(string? line)
    {
        var original = Console.In;
        try
        {
            // A StringReader over "" makes Console.ReadLine() return null immediately (true
            // EOF), same as a closed pipe — exercises the same path as the panel/process going
            // away mid-prompt.
            Console.SetIn(new StringReader(line is null ? string.Empty : line + "\n"));
            return ReplJsonBridge.ReadApprovalResponse();
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public void ReadApprovalResponse_Approved_ReturnsTrue() =>
        Assert.True(ReadApprovalResponseWithStdin("""{"type":"approval_response","approved":true}"""));

    [Fact]
    public void ReadApprovalResponse_Denied_ReturnsFalse() =>
        Assert.False(ReadApprovalResponseWithStdin("""{"type":"approval_response","approved":false}"""));

    [Fact]
    public void ReadApprovalResponse_WrongType_DeniesRatherThanMisreadsAsApproval() =>
        Assert.False(ReadApprovalResponseWithStdin("""{"type":"user_input","text":"yes"}"""));

    [Fact]
    public void ReadApprovalResponse_MalformedJson_DeniesRatherThanThrows() =>
        Assert.False(ReadApprovalResponseWithStdin("not json at all"));

    [Fact]
    public void ReadApprovalResponse_MissingApprovedField_Denies() =>
        Assert.False(ReadApprovalResponseWithStdin("""{"type":"approval_response"}"""));

    [Fact]
    public void ReadApprovalResponse_Eof_DeniesRatherThanThrows() =>
        Assert.False(ReadApprovalResponseWithStdin(null));

    [Fact]
    public async Task PromptShellCommandAsync_RelaysParsedApprovalFromStdin()
    {
        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader("""{"type":"approval_response","approved":true}""" + "\n"));
            var service = new JsonBridgeHumanApprovalService();

            var allowed = await service.PromptShellCommandAsync("echo hi");

            Assert.True(allowed);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public async Task PromptShellCommandAsync_DeniedResponse_ReturnsFalse()
    {
        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader("""{"type":"approval_response","approved":false}""" + "\n"));
            var service = new JsonBridgeHumanApprovalService();

            var allowed = await service.PromptShellCommandAsync("rm -rf /");

            Assert.False(allowed);
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}
