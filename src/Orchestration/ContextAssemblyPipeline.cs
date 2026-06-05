using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration;

/// <summary>
/// Single entry point for all agent context construction.
///
/// <para>Pipeline stages:</para>
/// <list type="number">
///   <item>System prompt — agent instructions + relevance-ranked memory block.</item>
///   <item>Intent analysis — keywords and symbols extracted from the task.</item>
///   <item>Knowledge retrieval — always-on query of the knowledge layer (unless <c>KnowledgeWeight.None</c>).</item>
///   <item>Graph expansion — one-hop neighbour traversal for <c>KnowledgeWeight.High</c> agents.</item>
///   <item>Context budgeting — rank artifacts by confidence, trim to limits.</item>
///   <item>Prompt construction — assemble the final message list.</item>
/// </list>
///
/// <para>Invariant: <c>ContextWindowFilter.Apply()</c> is never called by orchestrators directly.
/// All history filtering happens inside this class.</para>
/// </summary>
public sealed class ContextAssemblyPipeline : IContextAssemblyPipeline
{
    private readonly IKnowledgeLayer?          _knowledgeLayer;
    private readonly KnowledgeRetriever?       _retriever;
    private readonly GraphExpansionRetriever?  _graphExpander;
    private readonly MemoryManager?            _memoryManager;
    private readonly IMemoryRanker             _memoryRanker;
    private readonly ContextAssembler?         _contextAssembler;
    private readonly ILogger?                  _logger;

    // Per-instance state, set by SetSessionId().
    private string _sessionId = string.Empty;

    // Knowledge artifact budget: 6 000 chars (~1 500 tokens).
    private const int KnowledgeBudgetChars = 6_000;

    public ContextAssemblyPipeline(
        IKnowledgeLayer?          knowledgeLayer    = null,
        MemoryManager?            memoryManager     = null,
        ContextAssembler?         contextAssembler  = null,
        IMemoryRanker?            memoryRanker      = null,
        GraphExpansionRetriever?  graphExpander     = null,
        RepositoryKnowledgeStore? knowledgeStore    = null,
        ILogger<ContextAssemblyPipeline>? logger    = null)
    {
        _knowledgeLayer   = knowledgeLayer;
        _retriever        = knowledgeLayer is not null
            ? new KnowledgeRetriever(knowledgeLayer, knowledgeStore: knowledgeStore)
            : null;
        _graphExpander    = graphExpander;
        _memoryManager    = memoryManager;
        _memoryRanker     = memoryRanker ?? new RelevanceMemoryRanker();
        _contextAssembler = contextAssembler;
        _logger           = logger;
    }

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _contextAssembler?.SetSessionId(sessionId);
    }

    public async Task<AssembledContext> AssembleAsync(
        AgentExecutionRequest request,
        CancellationToken     ct = default)
    {
        var sw         = Stopwatch.StartNew();
        var agentName  = request.AgentName;
        var task       = request.Task;
        var history    = request.SharedHistory;
        var agentCfg   = request.AgentConfig;
        var weight     = agentCfg?.KnowledgeWeight ?? KnowledgeWeight.Default;

        // ── Stage 1: Intent Analysis ─────────────────────────────────────────
        var signals = IntentAnalyzer.Analyze(task);

        // ── Stage 2: Memory Block ────────────────────────────────────────────
        var (memoryBlock, memLoaded, memIncluded) =
            await BuildMemoryBlockAsync(agentName, signals, ct);

        // ── Stage 3: System Prompt ───────────────────────────────────────────
        var baseInstructions = agentCfg?.Instructions ?? string.Empty;
        var augmentedInstr   = request.AdditionalInstructions is { Length: > 0 } extra
            ? (string.IsNullOrWhiteSpace(baseInstructions) ? extra : $"{baseInstructions}\n\n{extra}")
            : baseInstructions;
        var systemPrompt = BuildSystemPrompt(augmentedInstr, memoryBlock);

        // ── Stage 4: Knowledge Retrieval ─────────────────────────────────────
        var knowledgeItems  = new List<KnowledgeItem>();
        var artifacts       = new List<ContextArtifact>();
        int knRetrieved     = 0;

        if (weight != KnowledgeWeight.None && _retriever is not null && !signals.IsEmpty)
        {
            var (retrieved, retrievedCount) = await RetrieveKnowledgeAsync(signals, weight, ct);
            knRetrieved = retrievedCount;
            knowledgeItems.AddRange(retrieved);

            if (knowledgeItems.Count > 0)
            {
                var block = FormatKnowledgeBlock(knowledgeItems);
                artifacts.Add(new ContextArtifact(
                    Type:     "knowledge",
                    Title:    "Retrieved Knowledge",
                    Content:  block,
                    Priority: 90));
            }
        }

        // ── Stage 5: History / Context Assembly ──────────────────────────────
        IReadOnlyList<ChatMessage> baseMessages;
        int sessionContextChars = 0;
        int historyChars        = 0;

        if (agentCfg?.Context is { Count: > 0 } contextSources && _contextAssembler is not null)
        {
            baseMessages = await _contextAssembler.AssembleForAgentAsync(
                agentName, task, contextSources, (IList<ChatMessage>)history, ct);
            historyChars = baseMessages.Sum(m => m.Text?.Length ?? 0);
        }
        else
        {
            var filtered   = ContextWindowFilter.Apply(history, agentCfg?.ContextWindow);
            historyChars   = filtered.Sum(m => m.Text?.Length ?? 0);
            var sessionCtx = _contextAssembler is not null
                ? await _contextAssembler.ReadSessionContextAsync(ct)
                : null;

            if (sessionCtx is not null)
            {
                sessionContextChars = sessionCtx.Length;
                artifacts.Add(new ContextArtifact(
                    Type:     "session_context",
                    Title:    "Session Context",
                    Content:  sessionCtx,
                    Priority: 100));
            }

            baseMessages = BuildDefaultMessages(filtered, sessionCtx);
        }

        // ── Stage 6: Artifact Injection ──────────────────────────────────────
        var finalMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            finalMessages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        finalMessages.AddRange(baseMessages);

        int knowledgeChars = 0;
        if (artifacts.Any(a => a.Type == "knowledge"))
        {
            bool hasExplicitBroker = agentCfg?.Context?.Any(s =>
                s.Source.StartsWith("broker", StringComparison.OrdinalIgnoreCase)) == true;

            if (!hasExplicitBroker)
            {
                var knowledgeArtifact = artifacts.First(a => a.Type == "knowledge");
                knowledgeChars = knowledgeArtifact.Content.Length;
                finalMessages.Add(new ChatMessage(ChatRole.User,
                    $"[Pipeline Knowledge]\n\n{knowledgeArtifact.Content}"));
            }
        }

        sw.Stop();
        var budget  = TokenBudget.Unlimited;
        var metrics = new ContextAssemblyMetrics
        {
            AgentName               = agentName,
            KnowledgeItemsRetrieved = knRetrieved,
            KnowledgeItemsIncluded  = knowledgeItems.Count,
            MemoryEntriesLoaded     = memLoaded,
            MemoryEntriesIncluded   = memIncluded,
            ArtifactsAssembled      = artifacts.Count,
            TotalContextChars       = finalMessages.Sum(m => m.Text?.Length ?? 0),
            SystemPromptChars       = systemPrompt.Length,
            MemoryChars             = memoryBlock?.Length ?? 0,
            SessionContextChars     = sessionContextChars,
            KnowledgeChars          = knowledgeChars,
            HistoryChars            = historyChars,
            AssemblyDuration        = sw.Elapsed,
        };

        _logger?.LogDebug(
            "[ContextPipeline] {Agent}: {MsgCount} messages, {KnIncluded}/{KnRetrieved} knowledge, " +
            "{MemIncluded}/{MemLoaded} memory, {ArtCount} artifacts | weight={Weight} | {Ms}ms",
            agentName, finalMessages.Count,
            metrics.KnowledgeItemsIncluded, metrics.KnowledgeItemsRetrieved,
            metrics.MemoryEntriesIncluded, metrics.MemoryEntriesLoaded,
            artifacts.Count, weight, (int)sw.Elapsed.TotalMilliseconds);

        return new AssembledContext(systemPrompt, finalMessages, artifacts, knowledgeItems, budget, metrics);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<(string? Block, int Loaded, int Included)> BuildMemoryBlockAsync(
        string        agentName,
        IntentSignals signals,
        CancellationToken ct)
    {
        if (_memoryManager is null) return (null, 0, 0);
        try
        {
            var store   = MemoryStore.ForAgent(agentName);
            var entries = await store.LoadAllAsync(ct);
            if (entries.Count == 0) return (null, 0, 0);

            var ranked   = _memoryRanker.Rank(entries, signals);
            var (block, included) = FormatMemoryBlock(ranked);
            return (block, entries.Count, included);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, 0, 0); }
    }

    private static (string? Block, int Included) FormatMemoryBlock(IReadOnlyList<MemoryEntry> entries)
    {
        if (entries.Count == 0) return (null, 0);

        const int MaxChars = 8_000;
        var sb        = new StringBuilder();
        var remaining = MaxChars;
        int included  = 0;

        sb.AppendLine("MEMORY — facts recalled from prior sessions:");
        foreach (var e in entries)
        {
            if (remaining <= 0) break;
            var header  = $"[{e.Type}] {e.Name}: {e.Description}";
            if (!string.IsNullOrWhiteSpace(e.Body))
            {
                var indented = string.Join("\n", e.Body.Split('\n').Select(l => $"  {l}"));
                var full     = $"{header}\n{indented}";
                if (full.Length <= remaining) { sb.AppendLine(full); remaining -= full.Length; }
                else                          { sb.AppendLine(header); remaining -= header.Length; }
            }
            else
            {
                sb.AppendLine(header);
                remaining -= header.Length;
            }
            included++;
        }

        var result = sb.ToString().TrimEnd();
        return result.Length > 0 ? (result, included) : (null, 0);
    }

    private static string BuildSystemPrompt(string instructions, string? memoryBlock)
    {
        if (string.IsNullOrWhiteSpace(memoryBlock))
            return instructions;
        if (string.IsNullOrWhiteSpace(instructions))
            return memoryBlock;
        return $"{instructions}\n\n{memoryBlock}";
    }

    // Returns (included items, total retrieved before budgeting).
    private async Task<(IReadOnlyList<KnowledgeItem> Items, int RetrievedCount)> RetrieveKnowledgeAsync(
        IntentSignals signals,
        KnowledgeWeight weight,
        CancellationToken ct)
    {
        var allSignals = signals;

        // Graph expansion: for High-weight agents, expand seed symbols one hop.
        if (weight >= KnowledgeWeight.High && _graphExpander is not null &&
            signals.ReferencedSymbols.Count > 0)
        {
            try
            {
                var expanded = await _graphExpander.ExpandAsync(signals.ReferencedSymbols, ct: ct);
                if (expanded.Count > 0)
                {
                    allSignals = new IntentSignals
                    {
                        Keywords          = signals.Keywords,
                        ReferencedSymbols = signals.ReferencedSymbols.Concat(expanded)
                                               .Distinct(StringComparer.OrdinalIgnoreCase)
                                               .Take(20)
                                               .ToList(),
                        FailurePatterns   = signals.FailurePatterns,
                    };
                }
            }
            catch { /* graph expansion is best-effort */ }
        }

        IReadOnlyList<RetrievedItem> rawItems;
        try   { rawItems = await _retriever!.RetrieveAsync(allSignals, ct); }
        catch { return ([], 0); }

        int retrievedCount = rawItems.Count;

        // For Low weight, only include high-confidence items.
        var filtered = weight == KnowledgeWeight.Low
            ? rawItems.Where(r => r.ConfidenceTier is "Verified" or "Inferred").ToList()
            : rawItems.Where(r => !r.IsExpired).ToList();

        // Budget to KnowledgeBudgetChars.
        var budgeted = ContextBudgeter.Budget(filtered, KnowledgeBudgetChars);

        var items = budgeted
            .Select(r => new KnowledgeItem(
                Id:         r.Result.Id,
                Kind:       r.Result.Kind.ToString(),
                Title:      r.Result.Title ?? string.Empty,
                Content:    r.Result.Summary ?? string.Empty,
                Confidence: TierToConfidence(r.ConfidenceTier)))
            .ToList();

        return (items, retrievedCount);
    }

    private static float TierToConfidence(string tier) => tier switch
    {
        "Verified" => 0.95f,
        "Inferred" => 0.80f,
        "Assumed"  => 0.60f,
        _          => 0.40f,
    };

    private static string FormatKnowledgeBlock(IReadOnlyList<KnowledgeItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Knowledge Broker — retrieved context]");

        var byKind = items.GroupBy(i => i.Kind, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in byKind.OrderBy(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine($"## {group.Key}");
            foreach (var item in group)
            {
                sb.Append($"- {item.Title}");
                if (!string.IsNullOrWhiteSpace(item.Content))
                    sb.Append($": {item.Content}");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    // Injects the session context file content at position 1 (after the first history
    // message) so the agent reads the current session state early in its context.
    private static IReadOnlyList<ChatMessage> BuildDefaultMessages(
        IReadOnlyList<ChatMessage> filtered,
        string?                   sessionCtx)
    {
        if (sessionCtx is null) return filtered;

        var result = new List<ChatMessage>(filtered.Count + 1);
        if (filtered.Count > 0) result.Add(filtered[0]);
        result.Add(new ChatMessage(ChatRole.User, $"[Session Context]\n\n{sessionCtx.Trim()}"));
        result.AddRange(filtered.Skip(1));
        return result;
    }
}
