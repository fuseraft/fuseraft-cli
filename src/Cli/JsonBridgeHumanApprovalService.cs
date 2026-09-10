using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Interfaces;

namespace fuseraft.Cli;

/// <summary>
/// Human approval service for the REPL's VS Code JSON-bridge mode (<c>fuseraft repl --vscode</c>).
/// <see cref="ConsoleHumanApprovalService"/> writes prompts via <c>AnsiConsole</c>/<c>Console.ReadLine</c>,
/// which the webview's JSON-line parser silently discards (non-JSON stdout) and can never answer
/// (it only ever writes JSON messages to stdin) — so under <c>/hitl on</c> every shell command
/// would appear to hang with no visible prompt and then resolve as denied. This service instead
/// emits an <c>approval_request</c> JSONL event the webview renders as an inline approve/deny UI,
/// and blocks for the matching <c>approval_response</c> JSONL reply (see
/// <see cref="ReplStdinPump.ReadApprovalResponseAsync"/>).
/// </summary>
public sealed class JsonBridgeHumanApprovalService(ReplStdinPump stdinPump) : IHumanApprovalService
{
    public async Task<bool> PromptShellCommandAsync(string command)
    {
        ReplJsonBridge.Emit(new { type = "approval_request", kind = "shell_command", command });
        return await stdinPump.ReadApprovalResponseAsync();
    }

    public async Task<bool> PromptToolActionAsync(string plugin, string action, string detail)
    {
        ReplJsonBridge.Emit(new { type = "approval_request", kind = "tool_action", plugin, action, detail });
        return await stdinPump.ReadApprovalResponseAsync();
    }

    public async Task<bool> PromptFileWriteAsync(string action, string path, string oldContent, string newContent)
    {
        ReplJsonBridge.Emit(new
        {
            type = "approval_request",
            kind = "file_write",
            plugin = "FileSystem",
            action,
            path,
            oldContent,
            newContent,
        });
        return await stdinPump.ReadApprovalResponseAsync();
    }

    // The REPL's /hitl mode only gates Shell/FileSystem/Git/Http mutating calls (see
    // ShellPlugin's approveCommand hook and FileSystemPlugin/GitPlugin/HttpPlugin's
    // approveAction hook in ReplCommand.cs) — none of the prompts below are reachable from the
    // webview today. They default to the same "no human available" behavior as
    // NonInteractiveHumanApprovalService rather than blocking on a console prompt the webview
    // has no UI for and could never answer.
    public Task<string?> PromptContinueAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptRedirectAsync(string agentName) => Task.FromResult<string?>(null);

    public Task<string?> PromptValidatorStuckAsync(
        string agentName, string validatorName, int consecutiveFailures, string lastError) =>
        Task.FromResult<string?>(null);

    public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage) =>
        Task.FromResult<string?>(null);

    public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent) =>
        Task.FromResult(true);

    public Task<string?> PromptPostSessionAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptPlanReviewAsync(string planText) => Task.FromResult<string?>(null);
}
