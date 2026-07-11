using fuseraft.Core.Interfaces;

namespace fuseraft.Cli;

/// <summary>
/// No-op human approval service for unattended contexts — eval suites, CI — where no
/// human is watching stdin.
///
/// <para>
/// <see cref="Cli.SessionRunner"/> escalates to <c>PromptBlockerResolutionAsync</c> /
/// <c>PromptValidatorStuckAsync</c> unconditionally whenever an agent is blocked or a
/// validator gets stuck — regardless of <c>hitlMode</c> — because that safety net is
/// meant to apply to ordinary interactive runs too, not just <c>--hitl</c> sessions.
/// <see cref="ConsoleHumanApprovalService"/> handles that by blocking on
/// <see cref="Console.ReadLine"/>. In a process with no attached TTY (an eval run, a
/// CI job) that read returns immediately as if Enter were pressed, so the escalation
/// still resolves — but only after printing a prompt that could never have been
/// answered, which reads as a hang in captured output. This service produces the same
/// "no human available, abort/pause" outcome deterministically and silently, without
/// depending on that EOF behavior or ever touching the console.
/// </para>
/// </summary>
public sealed class NonInteractiveHumanApprovalService : IHumanApprovalService
{
    public Task<string?> PromptContinueAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptRedirectAsync(string agentName) => Task.FromResult<string?>(null);

    public Task<string?> PromptValidatorStuckAsync(
        string agentName, string validatorName, int consecutiveFailures, string lastError) =>
        Task.FromResult<string?>(null);

    public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage) =>
        Task.FromResult<string?>(null);

    // No human is available to gate these, so default to permissive rather than
    // deadlocking a route or shell command that a human simply wasn't there to approve.
    public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent) =>
        Task.FromResult(true);

    public Task<bool> PromptShellCommandAsync(string command) => Task.FromResult(true);

    public Task<string?> PromptPostSessionAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptPlanReviewAsync(string planText) => Task.FromResult<string?>(null);
}
