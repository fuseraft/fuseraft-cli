using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Summarises older conversation turns into a single context message using an LLM,
/// retaining only the most recent turns verbatim.
///
/// The summary <see cref="AgentMessage"/> is given <c>Role = "user"</c> so it is
/// re-injected into the group chat as context the agents can read.  Its
/// <see cref="AgentMessage.Usage"/> carries the cumulative cost of all compacted turns
/// (plus the cost of the summary call itself) so that budget tracking remains exact
/// across compaction boundaries.
/// </summary>
public sealed class ConversationCompactor(
    IChatClient chatClient,
    CompactionConfig config,
    ILogger<ConversationCompactor> logger,
    string? resumptionNote = null,
    string? changeLogPath = null,
    IntentLog? intentLog = null,
    string? eventsLogPath = null,
    EvidenceStore? evidenceStore = null)
{
    // Tracks savings ratios from the last AntiThrashWindow compactions so we can detect
    // conversations that are thrashing (repeatedly compacting but saving very little).
    private readonly Queue<double> _recentSavings = new();
    /// <summary>
    /// Returns true when the current mode is <c>window</c>.
    /// In window mode compaction is token-budget-based; no LLM call is made.
    /// </summary>
    public bool IsWindowMode =>
        (config.Mode ?? "llm").Equals("window", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="messages"/> has reached or exceeded
    /// the configured trigger. In <c>window</c> mode the trigger is the estimated
    /// token count vs <see cref="CompactionConfig.TokenBudget"/>; in all other
    /// modes it is the assistant-turn count vs <see cref="CompactionConfig.TriggerTurnCount"/>.
    /// </summary>
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages)
    {
        if (IsWindowMode)
            return messages.Sum(m => (m.Content?.Length ?? 0) / 4) > config.TokenBudget;
        if (IsAntiThrashed()) return false;
        return messages.Count(m => m.Role == "assistant") >= config.TriggerTurnCount;
    }

    /// <summary>
    /// Overload for callers that maintain a running assistant-turn counter,
    /// avoiding a full list scan. Only valid when not in window mode.
    /// </summary>
    public bool ShouldCompact(int assistantTurnCount)
    {
        if (IsWindowMode) return false;
        if (IsAntiThrashed()) return false;
        return assistantTurnCount >= config.TriggerTurnCount;
    }

    /// <summary>
    /// Drops the oldest user+assistant pairs from <paramref name="messages"/> until
    /// the estimated token count is within <see cref="CompactionConfig.TokenBudget"/>.
    /// No LLM call is made; no summary message is injected.
    /// </summary>
    public IReadOnlyList<AgentMessage> TrimToWindow(IReadOnlyList<AgentMessage> messages)
    {
        var list  = messages.ToList();
        var total = list.Sum(m => (m.Content?.Length ?? 0) / 4);
        if (total <= config.TokenBudget) return list;

        // Skip pinned messages (compaction summaries) — they're already compact and
        // losing them would discard context that can't be recovered.
        int start = 0;
        while (start < list.Count && list[start].IsCompactionSummary) start++;

        while (total > config.TokenBudget && start + 1 < list.Count)
        {
            if (list[start].Role == "user")
            {
                total -= (list[start].Content?.Length ?? 0) / 4;
                list.RemoveAt(start);
            }
            if (start < list.Count && list[start].Role == "assistant")
            {
                total -= (list[start].Content?.Length ?? 0) / 4;
                list.RemoveAt(start);
            }
        }
        return list;
    }

    /// <summary>
    /// Compacts <paramref name="messages"/> into a summary plus a retained tail.
    /// When <paramref name="snapshotter"/> is provided and <see cref="CompactionConfig.Mode"/>
    /// is <c>lossless</c> or <c>hybrid</c>, durable evidence reconstruction replaces or
    /// augments the LLM-generated summary.
    /// </summary>
    public async Task<(AgentMessage Summary, IReadOnlyList<AgentMessage> Retained)> CompactAsync(
        string task,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default,
        IContextSnapshotter? snapshotter = null)
    {
        if (messages.Count < 2)
            throw new ArgumentException("Cannot compact a message list with fewer than 2 messages.", nameof(messages));

        var keepCount  = Math.Clamp(config.KeepRecentTurns, 1, messages.Count - 1);
        var toCompact  = messages.Take(messages.Count - keepCount).ToList();
        var toRetain   = messages.Skip(messages.Count - keepCount).ToList();

        // Record savings ratio now so it's captured regardless of which compaction path we take.
        // toCompact.Count messages become 1 summary; net reduction = toCompact.Count - 1.
        RecordSavings((toCompact.Count - 1.0) / messages.Count);

        logger.LogInformation(
            "Compacting {Compacted} turns (0–{LastCompacted}) into a summary; retaining {Kept} recent turns.",
            toCompact.Count, toCompact[^1].TurnIndex, toRetain.Count);

        var mode = (config.Mode ?? "llm").ToLowerInvariant();

        var reasoningExcerpts = await ReadReasoningForRangeAsync(
            toCompact[0].TurnIndex, toCompact[^1].TurnIndex);
        var reasoningBlock  = BuildReasoningBlock(reasoningExcerpts);
        var symbolBlock     = await BuildSymbolGraphBlockAsync(cancellationToken);
        var prefixBlock     = CombineBlocks(symbolBlock, reasoningBlock);

        // Intent mode: reconstruct from the intent log — fully deterministic, no LLM call.
        if (mode == "intent")
        {
            if (intentLog is not null)
            {
                var intents = await intentLog.GetIntentsForRangeAsync(
                    toCompact[0].TurnIndex, toCompact[^1].TurnIndex, cancellationToken);
                var intentSummary = BuildIntentDerivedSummary(
                    toCompact[0].TurnIndex, toCompact[^1].TurnIndex, intents, prefixBlock);
                logger.LogInformation(
                    "Intent compaction: {Compacted} turns replaced by intent log reconstruction ({IntentCount} intents).",
                    toCompact.Count, intents.Count);
                return (intentSummary, toRetain);
            }

            logger.LogWarning(
                "Compaction mode is 'intent' but no intent log is available — falling back to lossless/llm.");
            // Fall through to lossless / llm.
        }

        // Lossless: skip LLM call entirely; rebuild from durable state.
        if ((mode == "lossless" || mode == "intent") && snapshotter is not null)
        {
            var snapshot      = await snapshotter.SnapshotAsync(cancellationToken);
            var reconstructed = ContextRebuilder.BuildContextMessage(snapshot, toCompact[^1].TurnIndex);
            if (!string.IsNullOrEmpty(prefixBlock))
                reconstructed = reconstructed with
                {
                    Content = prefixBlock + "\n\n---\n\n" + reconstructed.Content
                };
            logger.LogInformation(
                "Lossless compaction: {Compacted} turns replaced by evidence reconstruction.",
                toCompact.Count);
            return (reconstructed, toRetain);
        }

        // Hybrid: prepend reconstruction before the LLM summary.
        if (mode == "hybrid" && snapshotter is not null)
        {
            var snapshot      = await snapshotter.SnapshotAsync(cancellationToken);
            var reconstructed = ContextRebuilder.BuildContextMessage(snapshot, toCompact[^1].TurnIndex);

            try
            {
                var histText = BuildHistoryText(toCompact, config.MaxCharsPerHistoryMessage);
                var clText   = ReadChangeLog();
                var (summText, summUsage) = await GenerateSummaryAsync(
                    task, histText, clText, toCompact.Count, cancellationToken);

                var hybridContent =
                    reconstructed.Content + "\n\n---\n\n" +
                    FormatSummaryContent(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, summText, prefixBlock);

                var hybridSummary = new AgentMessage
                {
                    AgentName           = "System",
                    Content             = hybridContent,
                    Role                = "user",
                    TurnIndex           = toCompact[^1].TurnIndex,
                    IsCompactionSummary = true,
                    Usage               = summUsage is not null
                        ? new TokenUsage(summUsage.InputTokens, summUsage.OutputTokens)
                        : null
                };

                logger.LogInformation(
                    "Hybrid compaction complete. Turns 0–{Last} replaced by evidence reconstruction + LLM summary.",
                    toCompact[^1].TurnIndex);
                return (hybridSummary, toRetain);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // LLM summary failed; return the lossless reconstruction alone so the session survives.
                logger.LogError(ex,
                    "Hybrid compaction: LLM summary call failed — returning lossless reconstruction only.");
                return (reconstructed, toRetain);
            }
        }

        // LLM mode (default) — existing behaviour.
        if (mode is "lossless" or "intent")
            logger.LogWarning(
                "Compaction mode is '{Mode}' but no snapshotter or intent log is available — falling back to LLM mode.",
                mode);

        var historyText   = BuildHistoryText(toCompact, config.MaxCharsPerHistoryMessage);
        var changeLogText = ReadChangeLog();

        try
        {
            var (summaryText, summaryUsage) = await GenerateSummaryAsync(
                task, historyText, changeLogText, toCompact.Count, cancellationToken);

            var summary = new AgentMessage
            {
                AgentName           = "System",
                Content             = FormatSummaryContent(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, summaryText, prefixBlock),
                Role                = "user",
                TurnIndex           = toCompact[^1].TurnIndex,
                IsCompactionSummary = true,
                Usage               = summaryUsage is not null
                    ? new TokenUsage(summaryUsage.InputTokens, summaryUsage.OutputTokens)
                    : null
            };

            logger.LogInformation(
                "Compaction complete. Turns 0–{Last} replaced by summary.",
                toCompact[^1].TurnIndex);

            return (summary, toRetain);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLM compaction failed; inserting fallback marker for turns {First}–{Last}.",
                toCompact[0].TurnIndex, toCompact[^1].TurnIndex);
            return (BuildFallbackSummary(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, ex.Message), toRetain);
        }
    }

    // Internals

    private AgentMessage BuildIntentDerivedSummary(
        int firstTurn,
        int lastTurn,
        IReadOnlyList<fuseraft.Core.Models.IntentEntry> intents,
        string prefixBlock = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[INTENT-DERIVED RECONSTRUCTION — covers turns {firstTurn + 1}–{lastTurn + 1}]");
        sb.AppendLine();
        sb.AppendLine("OPERATIONS (chronological):");

        if (intents.Count == 0)
        {
            sb.AppendLine("  (no tracked tool calls recorded in this range)");
        }
        else
        {
            foreach (var intent in intents)
            {
                var icon   = intent.Status == fuseraft.Core.Models.IntentStatus.Applied ? "✓"
                           : intent.Status == fuseraft.Core.Models.IntentStatus.Failed  ? "✗"
                           : "⧖"; // hourglass for pending/retryable
                var target = intent.Operation.TargetPath is { } p ? $" → \"{p}\"" : string.Empty;
                var detail = intent.Status == fuseraft.Core.Models.IntentStatus.Failed && intent.ErrorMessage is { } err
                    ? $" — {err}"
                    : string.Empty;

                sb.AppendLine(
                    $"  {icon} {intent.Operation.FunctionName}{target}" +
                    $" (turn {intent.TurnIndex + 1}, {intent.Agent}){detail}");
            }
        }

        var pending = intents.Count(e => e.Status == fuseraft.Core.Models.IntentStatus.Pending);
        if (pending > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"WARNING: {pending} intent(s) are still PENDING — they may have been interrupted.");
            sb.AppendLine("Check current disk state before retrying these operations.");
        }

        sb.AppendLine();
        sb.Append(
            "RESUMPTION NOTE: History compacted from intent log — deterministic ground truth. " +
            "Do not re-execute operations marked ✓ (applied). " +
            "Operations marked ✗ (failed) should be retried if the task requires them.");

        if (resumptionNote is not null)
            sb.Append("\n\n---\n" + resumptionNote);

        var content = sb.ToString().TrimEnd();
        if (!string.IsNullOrEmpty(prefixBlock))
            content = prefixBlock + "\n\n---\n\n" + content;

        return new AgentMessage
        {
            AgentName           = "System",
            Content             = content,
            Role                = "user",
            TurnIndex           = lastTurn,
            IsCompactionSummary = true,
        };
    }

    private string? ReadChangeLog()
    {
        if (changeLogPath is null) return null;
        try { return File.ReadAllText(changeLogPath); }
        catch { return null; }
    }

    private async Task<(string Text, TokenUsage? Usage)> GenerateSummaryAsync(
        string task,
        string historyText,
        string? changeLogText,
        int turnCount,
        CancellationToken cancellationToken)
    {
        var changeLogBlock = changeLogText is not null
            ? $"""
              AUTHORITATIVE CHANGE LOG — ground truth of what was actually executed and written.
              Where the conversation contradicts this log, trust the log. Agent success claims are
              unreliable; exit codes and file writes recorded here are not:

              {changeLogText}

              """
            : string.Empty;

        var template = !string.IsNullOrWhiteSpace(config.SummaryTemplate)
            ? config.SummaryTemplate
            : SummaryPrompt;
        var prompt = template
            .Replace("{{$task}}",        task)
            .Replace("{{$turn_count}}",  turnCount.ToString())
            .Replace("{{$change_log}}", changeLogBlock)
            .Replace("{{$history}}",     historyText);

        ChatResponse result;
        try
        {
            result = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Compaction failed: the summary LLM call did not complete successfully. " +
                $"Inner: {ex.Message}", ex);
        }

        var text = result.Text?.Trim();

        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException(
                "Compaction failed: the summary LLM returned an empty response.");

        return (text, ExtractUsage(result));
    }

    private static TokenUsage? ExtractUsage(ChatResponse result)
    {
        if (result.Usage is null) return null;

        var inputTokens  = (int)(result.Usage.InputTokenCount  ?? 0L);
        var outputTokens = (int)(result.Usage.OutputTokenCount ?? 0L);

        if (inputTokens == 0 && outputTokens == 0) return null;

        return new TokenUsage(inputTokens, outputTokens);
    }

    private static string BuildHistoryText(IReadOnlyList<AgentMessage> messages, int maxCharsPerMessage)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var label = msg.IsCompactionSummary
                ? $"[Prior Summary — covers turns 1–{msg.TurnIndex + 1}]"
                : $"[{(msg.Role == "user" ? "Human" : msg.AgentName)} — Turn {msg.TurnIndex + 1}]";

            sb.AppendLine(label);
            sb.AppendLine(PruneContent(msg, maxCharsPerMessage));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Truncates long message content before passing it to the LLM summarizer so a single
    // verbose turn cannot dominate the history text. Compaction summaries are never truncated.
    // When tool calls were recorded for the turn, appends a compact call list so the summarizer
    // still knows what operations were attempted even after truncation.
    private static string PruneContent(AgentMessage msg, int maxChars)
    {
        if (msg.IsCompactionSummary || maxChars <= 0 || msg.Content.Length <= maxChars)
            return msg.Content;

        var truncated = msg.Content[..maxChars] + $" [TRUNCATED — {msg.Content.Length:N0} chars total]";

        if (msg.ToolCalls is { Count: > 0 } calls)
        {
            var toolList = string.Join(", ", calls.Select(tc =>
                $"{(tc.Succeeded ? "✓" : "✗")} {tc.Name}" +
                (tc.ArgsSummary is not null ? $"({tc.ArgsSummary})" : string.Empty)));
            truncated += $"\n  [Tool calls: {toolList}]";
        }

        return truncated;
    }

    // Returns true when every entry in the recent-savings window is below the configured
    // minimum ratio, signalling that repeated compactions are not meaningfully reducing size.
    private bool IsAntiThrashed()
    {
        if (config.AntiThrashWindow <= 0 || config.AntiThrashMinSavingsRatio <= 0) return false;
        if (_recentSavings.Count < config.AntiThrashWindow) return false;
        return _recentSavings.All(r => r < config.AntiThrashMinSavingsRatio);
    }

    private void RecordSavings(double ratio)
    {
        _recentSavings.Enqueue(ratio);
        while (_recentSavings.Count > Math.Max(1, config.AntiThrashWindow))
            _recentSavings.Dequeue();
    }

    private AgentMessage BuildFallbackSummary(int firstTurn, int lastTurn, string errorMessage)
    {
        var content =
            $"[COMPACTION FAILED — covers turns {firstTurn + 1}–{lastTurn + 1}]\n\n" +
            $"Summary generation failed: {errorMessage}\n\n" +
            "Context for this turn range could not be preserved. Before acting:\n" +
            "• Read current file state directly — do not assume prior work was completed.\n" +
            "• Check the change log for ground truth of what was actually written.\n" +
            "• Re-derive your next step from observable disk state, not from memory.";

        if (resumptionNote is not null)
            content += "\n\n---\n" + resumptionNote;

        return new AgentMessage
        {
            AgentName           = "System",
            Content             = content,
            Role                = "user",
            TurnIndex           = lastTurn,
            IsCompactionSummary = true,
        };
    }

    /// <summary>
    /// Resumption note appended to compaction summaries in workflow/agent sessions.
    /// Instructs agents to re-orient from brief.json and the change log before acting.
    /// Not appropriate for Magentic sessions, which have no brief.json; pass
    /// <c>resumptionNote: null</c> to the constructor to omit the footer entirely.
    /// </summary>
    public const string WorkflowResumptionNote =
        "RESUMPTION NOTE: History compacted. Before acting: " +
        "(1) read_file .fuseraft/brief.json, " +
        "(2) changes_read_latest to confirm what is already done, " +
        "(3) do not redo work changes.json confirms is complete.";

    private string FormatSummaryContent(int firstTurn, int lastTurn, string summaryText, string prefixBlock = "")
    {
        var prefixSection = !string.IsNullOrEmpty(prefixBlock)
            ? prefixBlock + "\n\n---\n\n"
            : string.Empty;
        var header = $"{prefixSection}[CONVERSATION SUMMARY — covers turns {firstTurn + 1}–{lastTurn + 1}]\n\n{summaryText}";
        return resumptionNote is not null
            ? $"{header}\n\n---\n{resumptionNote}"
            : header;
    }

    private async Task<IReadOnlyList<(int Turn, string Agent, string Text)>> ReadReasoningForRangeAsync(
        int firstTurn, int lastTurn)
    {
        if (!config.IncludeReasoning || eventsLogPath is null) return [];

        var results = new List<(int, string, string)>();
        try
        {
            if (!File.Exists(eventsLogPath)) return [];
            foreach (var line in await File.ReadAllLinesAsync(eventsLogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event_type", out var et) || et.GetString() != "reasoning") continue;
                    if (!root.TryGetProperty("turn", out var turnEl) || !turnEl.TryGetInt32(out var turn)) continue;
                    if (turn < firstTurn || turn > lastTurn) continue;
                    var text  = root.TryGetProperty("payload", out var payload)
                        && payload.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var agent = root.TryGetProperty("agent", out var agentEl)
                        ? agentEl.GetString() ?? string.Empty : string.Empty;
                    results.Add((turn, agent, text));
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { /* skip unreadable file */ }
        return results;
    }

    private static string BuildReasoningBlock(IReadOnlyList<(int Turn, string Agent, string Text)> excerpts)
    {
        if (excerpts.Count == 0) return string.Empty;

        const int MaxCharsPerExcerpt = 2_000; // ~500 tokens
        var sb = new StringBuilder();
        sb.AppendLine("[REASONING EXCERPTS — model thinking for compacted turns]");
        sb.AppendLine();
        foreach (var (turn, agent, text) in excerpts.OrderBy(e => e.Turn))
        {
            var truncated = text.Length > MaxCharsPerExcerpt
                ? text[..MaxCharsPerExcerpt] + $" [TRUNCATED — {text.Length:N0} chars total]"
                : text;
            sb.AppendLine($"Turn {turn + 1} ({agent}): {truncated}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Combines symbolBlock and reasoningBlock into a single prefix, separated by a divider
    // when both are non-empty. Symbol graph comes first so the dependency map frames the
    // reasoning excerpts that follow.
    private static string CombineBlocks(string symbolBlock, string reasoningBlock)
    {
        if (string.IsNullOrEmpty(symbolBlock) && string.IsNullOrEmpty(reasoningBlock))
            return string.Empty;
        if (string.IsNullOrEmpty(symbolBlock)) return reasoningBlock;
        if (string.IsNullOrEmpty(reasoningBlock)) return symbolBlock;
        return symbolBlock + "\n\n---\n\n" + reasoningBlock;
    }

    private static readonly JsonSerializerOptions ChangeLogJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Queries the evidence store for symbol dependency nodes across all files changed during
    // the active session. Returns an empty string when IncludeSymbolGraph is false, the store
    // is absent, or no symbol nodes are found.
    private async Task<string> BuildSymbolGraphBlockAsync(CancellationToken ct)
    {
        if (!config.IncludeSymbolGraph || evidenceStore is null) return string.Empty;

        var changedFiles = await LoadAllChangedFilesAsync(ct);
        if (changedFiles.Count == 0) return string.Empty;

        var nodesByFile = new Dictionary<string, List<EvidenceNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var nodes = await evidenceStore.QuerySymbolDependenciesAsync(file, ct);
            if (nodes.Count == 0) continue;
            nodesByFile[file] = [..nodes];
        }

        return BuildSymbolGraphText(nodesByFile);
    }

    private static string BuildSymbolGraphText(Dictionary<string, List<EvidenceNode>> nodesByFile)
    {
        if (nodesByFile.Count == 0) return string.Empty;

        var totalNodes = nodesByFile.Values.Sum(v => v.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"[SYMBOL DEPENDENCY GRAPH — {totalNodes} node(s) across {nodesByFile.Count} file(s)]");
        sb.AppendLine();

        foreach (var (file, nodes) in nodesByFile.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"File: {file}");
            foreach (var node in nodes.OrderBy(n => n.NodeType).ThenBy(n => n.SymbolName))
            {
                if (string.Equals(node.NodeType, "SymbolDefinition", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = string.IsNullOrEmpty(node.SymbolKind) ? "" : $" ({node.SymbolKind})";
                    sb.AppendLine($"  SymbolDefinition{kind}: {node.SymbolName}");
                }
                else if (string.Equals(node.NodeType, "SymbolReference", StringComparison.OrdinalIgnoreCase))
                {
                    var target = string.IsNullOrEmpty(node.TargetFile) ? "" : $" → {node.TargetFile}";
                    sb.AppendLine($"  SymbolReference: {node.SymbolName}{target}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // Reads all unique file paths written across every change-log entry for the active session.
    private async Task<IReadOnlyList<string>> LoadAllChangedFilesAsync(CancellationToken ct)
    {
        if (changeLogPath is null || !File.Exists(changeLogPath)) return [];

        try
        {
            var json = await File.ReadAllTextAsync(changeLogPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, ChangeLogJsonOpts);
            if (log is null) return [];

            var sessionId = log.ActiveSessionId;
            return log.Entries
                .Where(e => sessionId is null || e.SessionId == sessionId)
                .SelectMany(e => e.FilesWritten)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private const string SummaryPrompt = """
        You are compacting an AI agent conversation to preserve context while reducing its size.

        Task: {{$task}}

        {{$change_log}}The following {{$turn_count}} turns are being replaced by this summary:

        {{$history}}

        Write a structured summary using EXACTLY these four sections. Nothing omitted here can be
        recovered later — do not paraphrase away specifics (exact file paths, exit codes, commit messages).

        ## Completed
        Every piece of work that is fully done: files written (exact paths), commands run with exit
        codes, git commits made, decisions finalized. Nothing listed here will be repeated.

        ## Open Questions
        Every question raised but not yet answered, every ambiguity unresolved, every decision
        deferred. If none, write "None."

        ## Remaining Work
        Everything started but not finished, and everything not yet started that the task requires.
        Include the exact next step for anything in-progress. If all work is complete, write "None."

        ## Key Findings
        Discoveries, constraints, error patterns, or facts that will affect future decisions:
        unexpected behavior found, workarounds applied, architectural decisions made, known
        limitations. If none, write "None."
        """;
}
