using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Failure;

/// <summary>
/// Classifies a routing validator failure into a <see cref="FailureType"/> so the
/// orchestrator can apply a targeted response rather than always escalating uniformly.
///
/// <para>
/// Classification is heuristic: it pattern-matches the validator's error message and
/// observes whether the agent made any tool calls between the previous error injection
/// and the current re-emission of the handoff keyword.
/// </para>
///
/// <para>
/// <b>Priority order:</b>
/// <list type="number">
///   <item><see cref="FailureType.NoProgress"/> — checked first because it overrides message
///       content: if the agent did nothing, the message text is irrelevant.</item>
///   <item><see cref="FailureType.ConflictingEvidence"/> — fake tests, hallucinated commands,
///       or inconsistent evidence; needs an audit rather than a retry.</item>
///   <item><see cref="FailureType.MissingEvidence"/> — required artifact absent from disk;
///       agent needs targeted instructions to create it.</item>
///   <item><see cref="FailureType.InvalidTransition"/> — catch-all for prerequisite failures
///       (no write, no shell pass).</item>
/// </list>
/// </para>
/// </summary>
public static class FailureClassifier
{
    // Phrases in validator error messages that indicate a required artifact is absent.
    private static readonly string[] MissingEvidenceMarkers =
    [
        "not found",
        "does not exist",
        "no file",
        "missing",
        "could not read",
        "failed to read",
        "brief not",
        "test report not",
    ];

    // Phrases that indicate evidence is internally inconsistent or fabricated.
    private static readonly string[] ConflictingEvidenceMarkers =
    [
        "fake",
        "hallucin",
        "inconsistent",
        "no evidence",
        "never ran",
        "never actually ran",
        "not in change log",
        "not recorded",
        "command was not run",
        "no record",
        "fabricated",
        "cannot be verified",
        "unverifiable",
    ];

    /// <summary>
    /// Classifies a validator failure.
    /// </summary>
    /// <param name="errorMessage">The error message returned by the failing validator.</param>
    /// <param name="agentMadeToolCalls">
    ///   <c>true</c> when the agent called at least one tool between the previous
    ///   error injection and this re-emission of the handoff keyword.
    ///   <c>false</c> when the agent produced no tool calls — indicating no-progress.
    /// </param>
    /// <param name="isFirstFailure">
    ///   <c>true</c> on the first failure (no prior injection). NoProgress is only
    ///   meaningful on subsequent failures; on the first one the agent has not yet
    ///   had a chance to act.
    /// </param>
    public static FailureType Classify(
        string errorMessage,
        bool agentMadeToolCalls,
        bool isFirstFailure)
    {
        // NoProgress: agent had an opportunity to act (not first failure) but didn't.
        if (!isFirstFailure && !agentMadeToolCalls)
            return FailureType.NoProgress;

        var lower = errorMessage.ToLowerInvariant();

        // ConflictingEvidence takes priority over MissingEvidence — inconsistency is
        // more severe than an absent artifact and needs a different remediation path.
        foreach (var marker in ConflictingEvidenceMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return FailureType.ConflictingEvidence;
        }

        foreach (var marker in MissingEvidenceMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return FailureType.MissingEvidence;
        }

        return FailureType.InvalidTransition;
    }
}
