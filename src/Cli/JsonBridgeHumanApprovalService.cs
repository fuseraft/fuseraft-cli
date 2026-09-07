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
/// <see cref="ReplJsonBridge.ReadApprovalResponse"/>).
/// </summary>
public sealed class JsonBridgeHumanApprovalService : IHumanApprovalService
{
    public Task<bool> PromptShellCommandAsync(string command)
    {
        ReplJsonBridge.Emit(new { type = "approval_request", kind = "shell_command", command });
        return Task.FromResult(ReplJsonBridge.ReadApprovalResponse());
    }

    // The REPL's /hitl mode only ever gates shell commands (see ShellPlugin's approveCommand
    // hook in ReplCommand.cs) — none of the prompts below are reachable from the webview today.
    // They default to the same "no human available" behavior as NonInteractiveHumanApprovalService
    // rather than blocking on a console prompt the webview has no UI for and could never answer.
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
