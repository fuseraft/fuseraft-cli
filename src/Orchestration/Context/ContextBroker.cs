using System.Text;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration.Context;

/// <summary>
/// Adaptive context broker — Gap 8 implementation.
///
/// <para>Pipeline: <c>IntentAnalyzer → KnowledgeRetriever → ContextBudgeter → Prompt Assembly</c></para>
///
/// <para>
/// Given a natural-language query or task description, the broker extracts intent signals,
/// queries all registered knowledge subsystems (ADR registry, repository semantic graph,
/// repository memory), ranks results by provenance confidence, trims to a character budget,
/// and returns a formatted context block ready for injection into an agent prompt.
/// </para>
///
/// <para>
/// Expired claims (past their <c>ExpiresAt</c>) are excluded from output. The broker
/// falls back gracefully to <c>null</c> (no content) when no relevant items are found.
/// </para>
/// </summary>
public sealed class ContextBroker
{
    private readonly KnowledgeRetriever _retriever;

    public ContextBroker(
        IKnowledgeLayer        knowledgeLayer,
        RepositoryMemoryStore? memoryStore  = null,
        ProvenanceRegistry?    provenance   = null)
    {
        _retriever = new KnowledgeRetriever(knowledgeLayer, memoryStore, provenance);
    }

    /// <summary>
    /// Runs the full broker pipeline for <paramref name="query"/> and returns a formatted
    /// context block, or <c>null</c> when no relevant knowledge is found.
    /// </summary>
    /// <param name="query">
    /// A natural-language query, keyword, or task description. When empty, the broker
    /// returns <c>null</c> without querying the knowledge layer.
    /// </param>
    /// <param name="maxChars">Character budget for the output. 0 = no limit.</param>
    public async Task<string?> ResolveAsync(
        string query,
        int maxChars = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var signals = IntentAnalyzer.Analyze(query);
        if (signals.IsEmpty)
            return null;

        var allItems = await _retriever.RetrieveAsync(signals, ct);
        if (allItems.Count == 0)
            return null;

        var budgeted = ContextBudgeter.Budget(allItems, maxChars);
        if (budgeted.Count == 0)
            return null;

        return Format(query, budgeted);
    }

    // Groups items by kind and confidence, then formats into a labelled block.
    private static string Format(string query, IReadOnlyList<RetrievedItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Knowledge Broker — adaptive context for: {Truncate(query, 80)}]");

        // Group: Decisions (ADRs)
        var decisions = items.Where(i => i.Result.Kind == KnowledgeKind.Decision).ToList();
        if (decisions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Architecture Decisions");
            foreach (var item in decisions)
                AppendItem(sb, item);
        }

        // Group: Graph nodes (symbols / files / types)
        var graphNodes = items.Where(i => i.Result.Kind == KnowledgeKind.GraphNode).ToList();
        if (graphNodes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository Symbols");
            foreach (var item in graphNodes)
                AppendItem(sb, item);
        }

        // Group: Repository memory (approved patterns)
        var memories = items.Where(i => i.Result.Kind == KnowledgeKind.Memory).ToList();
        if (memories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository Memory");
            foreach (var item in memories)
                AppendItem(sb, item);
        }

        // Group: Claims and objectives
        var rest = items
            .Where(i => i.Result.Kind is not KnowledgeKind.Decision
                                      and not KnowledgeKind.GraphNode
                                      and not KnowledgeKind.Memory)
            .ToList();
        if (rest.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Other Knowledge");
            foreach (var item in rest)
                AppendItem(sb, item);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendItem(StringBuilder sb, RetrievedItem item)
    {
        var r = item.Result;
        var confidence = item.ConfidenceTier != "Guessed"
            ? $" [{item.ConfidenceTier}]"
            : string.Empty;
        var status = r.Status is not null ? $" (status: {r.Status})" : string.Empty;

        sb.Append($"- {r.Title}{confidence}{status}");
        if (!string.IsNullOrWhiteSpace(r.FilePath))
            sb.Append($" — {r.FilePath}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(r.Summary))
            sb.AppendLine($"  {r.Summary}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
