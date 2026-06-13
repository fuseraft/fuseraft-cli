using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// Derives candidate <see cref="RepositoryMemoryEntry"/> records from the evidence graph
/// after a session closes.
///
/// <para>
/// All extraction is deterministic — no LLM call is made. Patterns are derived from:
/// <list type="bullet">
///   <item>Shell commands that exited successfully (<see cref="EvidenceClass.ExitCode"/>)</item>
///   <item>Test results that passed (<see cref="EvidenceClass.TestResult"/>)</item>
///   <item>Files written more than once in a session (<see cref="EvidenceClass.EvidenceGraph"/>)</item>
///   <item>Shell commands that exited non-zero more than once — flags precondition problems</item>
/// </list>
/// </para>
///
/// <para>
/// New candidates are written with <c>Status = Candidate</c>. When the same pattern
/// recurs in a later session, <see cref="RepositoryMemoryEntry.ReinforcementCount"/> is
/// incremented regardless of whether the entry is <c>Approved</c> or still <c>Candidate</c>.
/// Promotion from <c>Candidate</c> to <c>Approved</c> still requires explicit human review
/// (<c>fuseraft memory review</c>) or a reviewer agent — reinforcement only makes
/// high-value candidates surface first in the index.
/// </para>
/// </summary>
public sealed class RepositoryMemoryExtractor
{
    private readonly EvidenceStore         _evidenceStore;
    private readonly RepositoryMemoryStore _memoryStore;

    public RepositoryMemoryExtractor(EvidenceStore evidenceStore, RepositoryMemoryStore memoryStore)
    {
        _evidenceStore = evidenceStore;
        _memoryStore   = memoryStore;
    }

    /// <summary>
    /// Extracts candidate memories from the evidence graph for the given session.
    /// Returns the new <c>Candidate</c> entries created; approved entries that were
    /// reinforced are not included in the returned list.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryMemoryEntry>> ExtractAsync(
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var commandNodes = await _evidenceStore.QueryNodes(
            n => n.NodeType == "CommandRun" && n.ExitCode == 0 &&
                 !string.IsNullOrWhiteSpace(n.Command) &&
                 (sessionId is null || n.SessionId == sessionId), ct);

        var testNodes = await _evidenceStore.QueryNodes(
            n => n.NodeType == "TestResult" &&
                 string.Equals(n.Status, "PASS", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(n.Criterion) &&
                 (sessionId is null || n.SessionId == sessionId), ct);

        var fileWriteNodes = await _evidenceStore.QueryNodes(
            n => n.NodeType == "FileWrite" &&
                 !string.IsNullOrWhiteSpace(n.Path) &&
                 (sessionId is null || n.SessionId == sessionId), ct);

        var existing      = await _memoryStore.LoadAllAsync(ct);
        var newCandidates = new List<RepositoryMemoryEntry>();
        var seenPatterns  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Successful shell commands
        foreach (var node in commandNodes)
        {
            var cmd     = node.Command!.Length > 120 ? node.Command[..120] + "…" : node.Command;
            var pattern = $"Shell command succeeds: {cmd}";
            if (seenPatterns.Add(pattern))
                await RecordOrReinforceAsync(pattern, [EvidenceClass.ExitCode],
                    existing, newCandidates, sessionId, ct);
        }

        // Passing test results
        foreach (var node in testNodes)
        {
            var pattern = $"Test passes: {node.Criterion}";
            if (seenPatterns.Add(pattern))
                await RecordOrReinforceAsync(pattern, [EvidenceClass.TestResult, EvidenceClass.ExitCode],
                    existing, newCandidates, sessionId, ct);
        }

        // Files written more than once (frequently modified)
        var writeCounts = fileWriteNodes
            .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in writeCounts)
        {
            var pattern = $"File is modified repeatedly in sessions: {group.Key}";
            if (seenPatterns.Add(pattern))
                await RecordOrReinforceAsync(pattern, [EvidenceClass.EvidenceGraph],
                    existing, newCandidates, sessionId, ct);
        }

        // Shell commands that failed more than once in a session. These flag precondition
        // problems, missing dependencies, or brittle invocations that future agents should
        // verify before relying on.
        var failedCommandNodes = await _evidenceStore.QueryNodes(
            n => n.NodeType == "CommandRun" && n.ExitCode != 0 &&
                 !string.IsNullOrWhiteSpace(n.Command) &&
                 (sessionId is null || n.SessionId == sessionId), ct);

        var failCounts = failedCommandNodes
            .GroupBy(n =>
            {
                var cmd = n.Command!;
                return cmd.Length > 120 ? cmd[..120] + "…" : cmd;
            }, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in failCounts)
        {
            var pattern = $"Shell command fails repeatedly: {group.Key}";
            if (seenPatterns.Add(pattern))
                await RecordOrReinforceAsync(pattern, [EvidenceClass.ExitCode],
                    existing, newCandidates, sessionId, ct);
        }

        return newCandidates;
    }

    // ── Reinforcement ────────────────────────────────────────────────────────

    private async Task RecordOrReinforceAsync(
        string pattern,
        List<EvidenceClass> evidence,
        List<RepositoryMemoryEntry> existing,
        List<RepositoryMemoryEntry> newCandidates,
        string? sessionId,
        CancellationToken ct)
    {
        // Reinforce an existing approved entry when the pattern matches.
        var approved = existing.FirstOrDefault(e =>
            e.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase) &&
            IsSamePattern(e.Pattern, pattern));

        if (approved is not null)
        {
            var merged = MergeEvidence(approved.Evidence, evidence);
            await _memoryStore.SaveAsync(approved with
            {
                ReinforcementCount = approved.ReinforcementCount + 1,
                LastReinforcedAt   = DateTimeOffset.UtcNow,
                Evidence           = merged,
                Confidence         = ConfidenceComputer.Compute(merged),
            }, ct);
            return;
        }

        // Reinforce an existing candidate when the same pattern recurs across sessions.
        // This does not promote the entry — promotion requires explicit review — but it
        // makes the cross-session signal visible so high-value candidates surface first.
        var candidate = existing.FirstOrDefault(e =>
            e.Status.Equals("Candidate", StringComparison.OrdinalIgnoreCase) &&
            IsSamePattern(e.Pattern, pattern) &&
            !string.Equals(e.SourceSessionId, sessionId, StringComparison.OrdinalIgnoreCase));

        if (candidate is not null)
        {
            var merged = MergeEvidence(candidate.Evidence, evidence);
            await _memoryStore.SaveAsync(candidate with
            {
                ReinforcementCount = candidate.ReinforcementCount + 1,
                LastReinforcedAt   = DateTimeOffset.UtcNow,
                Evidence           = merged,
            }, ct);
            return;
        }

        // Skip exact duplicates from the same session.
        if (existing.Any(e =>
            e.Status.Equals("Candidate", StringComparison.OrdinalIgnoreCase) &&
            IsSamePattern(e.Pattern, pattern) &&
            string.Equals(e.SourceSessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
            return;

        var entry = new RepositoryMemoryEntry
        {
            Pattern         = pattern,
            Evidence        = evidence,
            Confidence      = ConfidenceComputer.Compute(evidence),
            Status          = "Candidate",
            SourceSessionId = sessionId,
        };
        await _memoryStore.SaveAsync(entry, ct);
        newCandidates.Add(entry);
    }

    private static bool IsSamePattern(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<EvidenceClass> MergeEvidence(List<EvidenceClass> existing, List<EvidenceClass> added)
    {
        var merged = new List<EvidenceClass>(existing);
        foreach (var e in added)
            if (!merged.Contains(e)) merged.Add(e);
        return merged;
    }
}
