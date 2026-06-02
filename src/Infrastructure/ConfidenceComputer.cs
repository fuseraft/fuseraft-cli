using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Maps a support composition to a confidence status tier.
///
/// <para>Tier rules (applied in order):</para>
/// <list type="bullet">
///   <item><b>Verified</b> — two or more of: <c>TestResult</c>, <c>ExitCode</c>, <c>Validator</c>, <c>GitHistory</c></item>
///   <item><b>Inferred</b> — one hard evidence source (<c>ADR</c>, <c>RepositoryMemory</c>, or single <c>Validator</c> / <c>ExitCode</c> / <c>TestResult</c> / <c>GitHistory</c>)</item>
///   <item><b>Assumed</b> — <c>AgentAssertion</c> only, no corroborating hard evidence</item>
///   <item><b>Guessed</b> — no support at all</item>
/// </list>
/// </summary>
public static class ConfidenceComputer
{
    private static readonly HashSet<EvidenceClass> HardEvidence =
    [
        EvidenceClass.TestResult,
        EvidenceClass.ExitCode,
        EvidenceClass.Validator,
        EvidenceClass.GitHistory,
    ];

    /// <summary>
    /// Applies time-based decay to a confidence status. When a <c>Verified</c> claim has
    /// no explicit <c>ExpiresAt</c> and its <c>VerifiedAt</c> timestamp is older than
    /// <paramref name="decayDays"/>, the status is downgraded to <c>Inferred</c>.
    /// Claims with explicit <c>ExpiresAt</c> are governed by <see cref="ProvenanceRegistry.IsValidAsync"/>,
    /// not by this method.
    /// </summary>
    public static string Decay(
        string              status,
        DateTimeOffset?     verifiedAt,
        DateTimeOffset?     expiresAt,
        int                 decayDays)
    {
        if (decayDays <= 0) return status;
        if (expiresAt.HasValue) return status;
        if (verifiedAt is null) return status;
        if (!status.Equals("Verified", StringComparison.OrdinalIgnoreCase)) return status;

        var age = DateTimeOffset.UtcNow - verifiedAt.Value;
        return age.TotalDays > decayDays ? "Inferred" : status;
    }

    /// <summary>
    /// Computes the confidence status string from the supplied evidence classes.
    /// The result matches the <see cref="ClaimRecord.Status"/> string values.
    /// </summary>
    public static string Compute(IReadOnlyList<EvidenceClass> support)
    {
        if (support.Count == 0) return "Guessed";

        int hardCount = support.Count(e => HardEvidence.Contains(e));

        if (hardCount >= 2)
            return "Verified";

        if (hardCount == 1 ||
            support.Any(e => e is EvidenceClass.ADR or EvidenceClass.RepositoryMemory))
            return "Inferred";

        if (support.All(e => e == EvidenceClass.AgentAssertion))
            return "Assumed";

        // Fallback: EvidenceGraph or any unrecognised class with no hard sources.
        return "Inferred";
    }
}
