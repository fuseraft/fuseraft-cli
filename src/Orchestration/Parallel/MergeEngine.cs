using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Parallel;

/// <summary>
/// Combines parallel branch outputs into a single block of <see cref="ChatMessage"/>s
/// that is injected into the shared history before the orchestrator transitions to the
/// join state.
/// </summary>
public static class MergeEngine
{
    /// <summary>
    /// Merges <paramref name="results"/> asynchronously according to <paramref name="config"/>.
    /// <para>
    /// <see cref="MergeStrategy.Ranked"/> and <see cref="MergeStrategy.SemanticDiff"/> delegate
    /// to <paramref name="agentRunner"/> when provided (and <see cref="MergeConfig.Agent"/> is
    /// set). When the runner is null or no agent is named, both fall back to
    /// <see cref="MergeStrategy.Union"/>.
    /// </para>
    /// </summary>
    /// <param name="config">Merge strategy and optional scoring-agent name.</param>
    /// <param name="results">One entry per parallel branch: (agent name, text output).</param>
    /// <param name="agentRunner">
    /// Async delegate that runs the merge agent. Receives the full context message list
    /// (system prompt + branch content) and returns the agent's text response.
    /// Null when the orchestrator has no merge agent available.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the agent runner.</param>
    public static async Task<IReadOnlyList<ChatMessage>> MergeAsync(
        MergeConfig config,
        IReadOnlyList<(string AgentName, string Output)> results,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? agentRunner = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
            return [];

        if (results.Count == 1)
            return [new ChatMessage(ChatRole.User, FormatBranch(results[0].AgentName, results[0].Output))];

        return config.Strategy switch
        {
            MergeStrategy.Union        => Union(results),
            MergeStrategy.Consensus    => Consensus(results, logger),
            MergeStrategy.Vote         => Vote(results, logger),
            MergeStrategy.Ranked       => await RankedAsync(results, agentRunner, logger, cancellationToken),
            MergeStrategy.SemanticDiff => await SemanticDiffAsync(results, agentRunner, logger, cancellationToken),
            MergeStrategy.Benchmark    => FallbackToUnion(MergeStrategy.Benchmark, results, logger),
            _                          => Union(results),
        };
    }

    /// <summary>
    /// Synchronous merge for strategies that do not require an agent call
    /// (Union, Consensus, Vote). For Ranked/SemanticDiff/Benchmark, prefer
    /// <see cref="MergeAsync"/>.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Merge(
        MergeConfig config,
        IReadOnlyList<(string AgentName, string Output)> results,
        ILogger? logger = null)
    {
        if (results.Count == 0)
            return [];

        if (results.Count == 1)
            return [new ChatMessage(ChatRole.User, FormatBranch(results[0].AgentName, results[0].Output))];

        return config.Strategy switch
        {
            MergeStrategy.Union      => Union(results),
            MergeStrategy.Consensus  => Consensus(results, logger),
            MergeStrategy.Vote       => Vote(results, logger),
            MergeStrategy.Ranked     => FallbackToUnion(MergeStrategy.Ranked,      results, logger),
            MergeStrategy.SemanticDiff => FallbackToUnion(MergeStrategy.SemanticDiff, results, logger),
            MergeStrategy.Benchmark  => FallbackToUnion(MergeStrategy.Benchmark,   results, logger),
            _                        => Union(results),
        };
    }

    // Union ────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ChatMessage> Union(
        IReadOnlyList<(string AgentName, string Output)> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[fuseraft: parallel merge — union]");
        foreach (var (name, output) in results)
        {
            sb.AppendLine();
            sb.AppendLine(FormatBranch(name, output));
        }
        return [new ChatMessage(ChatRole.User, sb.ToString().TrimEnd())];
    }

    // Consensus ────────────────────────────────────────────────────────────────
    // Simple heuristic: if all branches share a non-trivial common substring (the last
    // non-empty line of each), treat them as agreed and emit a single consensus block.
    // Falls back to union on disagreement.

    private static IReadOnlyList<ChatMessage> Consensus(
        IReadOnlyList<(string AgentName, string Output)> results,
        ILogger? logger)
    {
        var lastLines = results
            .Select(r => LastMeaningfulLine(r.Output))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lastLines.Count == 1)
        {
            // All branches agree on their final statement.
            var sb = new StringBuilder();
            sb.AppendLine("[fuseraft: parallel merge — consensus reached]");
            sb.AppendLine();
            sb.AppendLine($"All branches agree: {lastLines[0]}");
            sb.AppendLine();
            sb.AppendLine("[branch outputs]");
            foreach (var (name, output) in results)
            {
                sb.AppendLine();
                sb.AppendLine(FormatBranch(name, output));
            }
            return [new ChatMessage(ChatRole.User, sb.ToString().TrimEnd())];
        }

        logger?.LogDebug(
            "[MergeEngine] Consensus: branches disagree on final statement — falling back to union");
        return Union(results);
    }

    // Vote ─────────────────────────────────────────────────────────────────────
    // Picks the last-line value that appears in the most branches.
    // Falls back to union on a tie.

    private static IReadOnlyList<ChatMessage> Vote(
        IReadOnlyList<(string AgentName, string Output)> results,
        ILogger? logger)
    {
        var tally = results
            .GroupBy(r => LastMeaningfulLine(r.Output), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (tally.Count > 0 && tally[0].Count() > (tally.Count > 1 ? tally[1].Count() : 0))
        {
            var winner = tally[0].Key;
            var sb = new StringBuilder();
            sb.AppendLine($"[fuseraft: parallel merge — vote winner: \"{winner}\"]");
            sb.AppendLine();
            foreach (var (name, output) in results)
            {
                sb.AppendLine();
                sb.AppendLine(FormatBranch(name, output));
            }
            return [new ChatMessage(ChatRole.User, sb.ToString().TrimEnd())];
        }

        logger?.LogDebug("[MergeEngine] Vote: tie — falling back to union");
        return Union(results);
    }

    // Ranked ───────────────────────────────────────────────────────────────────
    // Presents all branch outputs to a scoring agent which selects or synthesises
    // the best result. Falls back to union when no agent runner is available.

    private static async Task<IReadOnlyList<ChatMessage>> RankedAsync(
        IReadOnlyList<(string AgentName, string Output)> results,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? agentRunner,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (agentRunner is null)
        {
            logger?.LogWarning(
                "[MergeEngine] Ranked: no agent runner available — falling back to union. " +
                "Set Merge.Agent in the transition config to enable ranked merging.");
            return Union(results);
        }

        var branchBlock = BuildBranchBlock(results);
        var context = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a merge coordinator evaluating parallel agent outputs for the same task. " +
                "Select the single best output, or synthesise the strongest elements from each branch " +
                "into one cohesive result. " +
                "Begin your response with a one-sentence rationale, then output the complete chosen or merged content."),
            new(ChatRole.User, branchBlock),
        };

        logger?.LogDebug("[MergeEngine] Ranked: invoking scoring agent");
        var mergedText = await agentRunner(context, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("[fuseraft: parallel merge — ranked]");
        sb.AppendLine();
        sb.AppendLine(mergedText.Trim());
        return [new ChatMessage(ChatRole.User, sb.ToString().TrimEnd())];
    }

    // SemanticDiff ─────────────────────────────────────────────────────────────
    // Presents all branch outputs to a resolver agent which identifies agreements,
    // resolves conflicts, and produces a single reconciled output.

    private static async Task<IReadOnlyList<ChatMessage>> SemanticDiffAsync(
        IReadOnlyList<(string AgentName, string Output)> results,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? agentRunner,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (agentRunner is null)
        {
            logger?.LogWarning(
                "[MergeEngine] SemanticDiff: no agent runner available — falling back to union. " +
                "Set Merge.Agent in the transition config to enable semantic-diff merging.");
            return Union(results);
        }

        var branchBlock = BuildBranchBlock(results);
        var context = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a merge coordinator reconciling outputs from parallel agents working on the same task. " +
                "Follow these steps:\n" +
                "1. Identify points of agreement across branches — preserve these verbatim.\n" +
                "2. Identify conflicts or contradictions — resolve each one, preferring correctness and completeness.\n" +
                "3. Identify unique contributions that appear in only one branch — incorporate the valuable ones.\n" +
                "4. Return a single unified output that represents the best possible synthesis of all branches. " +
                "Do not include commentary about the merge process itself in the final output — only the reconciled content."),
            new(ChatRole.User, branchBlock),
        };

        logger?.LogDebug("[MergeEngine] SemanticDiff: invoking resolver agent");
        var mergedText = await agentRunner(context, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("[fuseraft: parallel merge — semantic_diff]");
        sb.AppendLine();
        sb.AppendLine(mergedText.Trim());
        return [new ChatMessage(ChatRole.User, sb.ToString().TrimEnd())];
    }

    // Helpers ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ChatMessage> FallbackToUnion(
        MergeStrategy requested,
        IReadOnlyList<(string AgentName, string Output)> results,
        ILogger? logger)
    {
        logger?.LogWarning(
            "[MergeEngine] Strategy '{Strategy}' is not implemented — falling back to union.",
            requested);
        return Union(results);
    }

    private static string BuildBranchBlock(IReadOnlyList<(string AgentName, string Output)> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PARALLEL BRANCH OUTPUTS:");
        foreach (var (name, output) in results)
        {
            sb.AppendLine();
            sb.AppendLine(FormatBranch(name, output));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatBranch(string agentName, string output) =>
        $"--- {agentName} ---\n{output.Trim()}";

    private static string LastMeaningfulLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Length > 0)
                return lines[i];
        return string.Empty;
    }
}
