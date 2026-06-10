namespace fuseraft.Core.Interfaces;

/// <summary>
/// Pauses orchestration to request input from a human operator.
/// Implementations handle console, Slack, Teams, or other delivery mechanisms.
/// </summary>
public interface IHumanApprovalService
{
    /// <summary>
    /// Prompts the user after each agent turn (--hitl mode).
    /// Returns null to continue, "\x00" to quit, or a non-empty string to inject as a redirect.
    /// </summary>
    Task<string?> PromptContinueAsync();

    /// <summary>
    /// Prompts the user when an agent is stuck on a validator and cannot proceed without help.
    /// Returns a redirect message to inject, or null to abort the session.
    /// </summary>
    Task<string?> PromptRedirectAsync(string agentName);

    /// <summary>
    /// Prompts the user when a validator has blocked an agent for too many consecutive turns,
    /// displaying the validator name, failure count, and last error. Returns a redirect message
    /// to inject, or null to pause the session.
    /// </summary>
    Task<string?> PromptValidatorStuckAsync(string agentName, string validatorName, int consecutiveFailures, string lastError);

    /// <summary>
    /// Prompts the user when an agent emits BLOCKED, displaying the blocker reason and
    /// asking for a resolution message to inject. Returns the message, or null to pause.
    /// </summary>
    Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage);

    /// <summary>
    /// Prompts for explicit approval before a route fires.
    /// Returns true if approved; false blocks the route and re-invokes the source agent.
    /// </summary>
    Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent);

    /// <summary>
    /// Shown once after the orchestrator terminates normally (termination condition met or
    /// max iterations reached) in --hitl mode.  Gives the user a chance to inject a follow-up
    /// message and keep the session alive instead of exiting silently.
    /// Returns null to exit, "\x00" to quit explicitly, or a non-empty string to inject.
    /// </summary>
    Task<string?> PromptPostSessionAsync();

    /// <summary>
    /// Called before a shell command executes in --hitl mode.
    /// Returns true to allow the command, false to block it (the plugin returns [DENIED]).
    /// </summary>
    Task<bool> PromptShellCommandAsync(string command);

    /// <summary>
    /// Prompts the human operator to review a Magentic manager-generated plan.
    /// Called when <c>Selection.Magentic.EnablePlanReview</c> is true.
    /// Returns null to approve the plan as-is, or a non-empty string as revision feedback
    /// which is fed back to the manager for replanning.
    /// </summary>
    Task<string?> PromptPlanReviewAsync(string planText);
}
