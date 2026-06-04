using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
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
    EvidenceStore? evidenceStore = null,
    fuseraft.Infrastructure.ObjectiveManager? objectiveManager = null,
    fuseraft.Infrastructure.KnowledgeSnapshotEnricher? knowledgeEnricher = null,
    string? readCachePath = null)
{
    // Tracks savings ratios from the last AntiThrashWindow compactions so we can detect
    // conversations that are thrashing (repeatedly compacting but saving very little).
    private readonly Queue<double> _recentSavings = new();
    private string _sessionId = string.Empty;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    private string? ExpandedNote =>
        resumptionNote is null ? null
        : _sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionId(resumptionNote, _sessionId)
            : resumptionNote;
    /// <summary>
    /// Returns true when the current mode is <c>window</c>.
    /// In window mode compaction is token-budget-based; no LLM call is made.
    /// </summary>
    public bool IsWindowMode =>
        (config.Mode ?? "llm").Equals("window", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="messages"/> has reached or exceeded
    /// the configured trigger. In <c>window</c> mode the trigger is the estimated
    /// token count (characters ÷ 4) vs <see cref="CompactionConfig.TokenBudget"/>, using
    /// the same estimate as <see cref="TrimToWindow"/> so the two stay in sync; in all
    /// other modes it is the assistant-turn count vs <see cref="CompactionConfig.TriggerTurnCount"/>.
    /// </summary>
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages)
    {
        if (IsWindowMode)
        {
            // Use the same chars/4 estimate as TrimToWindow so the trigger and the trim
            // measure the same quantity. Usage.TotalTokens is the cumulative API call cost
            // (InputTokens = full context at that turn, not just this message), so summing
            // it across messages grows quadratically and diverges from the char-based budget
            // that TokenBudget is calibrated against — causing the trigger to fire while
            // TrimToWindow finds nothing to drop.
            var estimated = messages.Sum(m => (m.Content?.Length ?? 0) / 4);
            if (estimated > config.TokenBudget)
            {
                logger.LogDebug(
                    "Compaction triggered (window): ~{Tokens:N0} tokens > budget {Budget:N0}.",
                    estimated, config.TokenBudget);
                return true;
            }
            return false;
        }
        if (IsAntiThrashed())
        {
            logger.LogWarning(
                "Compaction skipped: anti-thrash guard triggered (last {Window} compactions saved < {Min:P0} each).",
                config.AntiThrashWindow, config.AntiThrashMinSavingsRatio);
            return false;
        }
        var assistantTurns = messages.Count(m => m.Role == "assistant");
        if (assistantTurns >= config.TriggerTurnCount)
        {
            logger.LogDebug(
                "Compaction triggered: {Turns} assistant turns >= threshold {Threshold}.",
                assistantTurns, config.TriggerTurnCount);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drops the oldest user+assistant pairs from <paramref name="messages"/> until
    /// the estimated token count (characters ÷ 4) is within <see cref="CompactionConfig.TokenBudget"/>.
    /// Uses the same estimation as <see cref="ShouldCompact"/> so the trigger and the
    /// trim always agree on when the budget is met.
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
        {
            logger.LogWarning("Compaction skipped: message list has {Count} message(s) — nothing to compact.", messages.Count);
            var passthrough = messages.Count == 1 ? messages[0] : new AgentMessage { Role = "user", Content = "(empty session)", AgentName = "System" };
            return (passthrough, []);
        }

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
        var reasoningBlock    = BuildReasoningBlock(reasoningExcerpts);
        var symbolBlock       = await BuildSymbolGraphBlockAsync(cancellationToken);
        var objectiveBlock    = await BuildObjectiveBlockAsync(cancellationToken);
        var explorationBlock  = await BuildExplorationBlockAsync(cancellationToken);
        var prefixBlock       = CombineBlocks(
                                    CombineBlocks(CombineBlocks(symbolBlock, objectiveBlock), reasoningBlock),
                                    explorationBlock);

        // Intent mode: reconstruct from the intent log — fully deterministic, no LLM call.
        // When the intent log is unavailable, record a visible fallback notice so agents
        // resuming after compaction know the summary was degraded.
        string? intentFallbackNotice = null;
        if (mode == "intent")
        {
            if (intentLog is not null)
            {
                var intents = await intentLog.GetIntentsForRangeAsync(
                    toCompact[0].TurnIndex, toCompact[^1].TurnIndex, cancellationToken);
                var intentSummary = BuildIntentDerivedSummary(
                    toCompact[0].TurnIndex, toCompact[^1].TurnIndex, intents, prefixBlock);
                intentSummary = intentSummary with
                {
                    Usage     = AccumulateCompactedUsage(toCompact, null),
                    ToolCalls = AccumulateCompactedToolCalls(toCompact),
                };
                logger.LogInformation(
                    "Intent compaction: {Compacted} turns replaced by intent log reconstruction ({IntentCount} intents).",
                    toCompact.Count, intents.Count);
                return (intentSummary, toRetain);
            }

            logger.LogWarning(
                "Compaction mode is 'intent' but no intent log is available — falling back to lossless/llm. " +
                "Configure ChangeTracking.IntentLogPath to enable deterministic intent compaction.");
            intentFallbackNotice =
                "[COMPACTION WARNING: 'intent' mode was requested but no intent log is wired — " +
                "this summary was generated using fallback compaction (lossless or LLM). " +
                "Configure ChangeTracking.IntentLogPath to suppress this warning.]";
            // Fall through to lossless / llm.
        }

        // Lossless: skip LLM call entirely; rebuild from durable state.
        if ((mode == "lossless" || mode == "intent") && snapshotter is not null)
        {
            var snapshot = await snapshotter.SnapshotAsync(cancellationToken);
            if (knowledgeEnricher is not null)
                snapshot = await knowledgeEnricher.EnrichAsync(snapshot, cancellationToken);
            var reconstructed = ContextRebuilder.BuildContextMessage(snapshot, toCompact[^1].TurnIndex);
            if (!string.IsNullOrEmpty(prefixBlock))
                reconstructed = reconstructed with
                {
                    Content = prefixBlock + "\n\n---\n\n" + reconstructed.Content
                };
            if (ExpandedNote is not null)
                reconstructed = reconstructed with { Content = reconstructed.Content + "\n\n---\n" + ExpandedNote };
            reconstructed = reconstructed with
            {
                Usage     = AccumulateCompactedUsage(toCompact, null),
                ToolCalls = AccumulateCompactedToolCalls(toCompact),
            };
            logger.LogInformation(
                "Lossless compaction: {Compacted} turns replaced by evidence reconstruction.",
                toCompact.Count);
            return (PrependFallbackNotice(reconstructed, intentFallbackNotice), toRetain);
        }

        // Hybrid: prepend reconstruction before the LLM summary.
        if (mode == "hybrid" && snapshotter is not null)
        {
            var snapshot = await snapshotter.SnapshotAsync(cancellationToken);
            if (knowledgeEnricher is not null)
                snapshot = await knowledgeEnricher.EnrichAsync(snapshot, cancellationToken);
            var reconstructed = ContextRebuilder.BuildContextMessage(snapshot, toCompact[^1].TurnIndex);

            try
            {
                var histText       = BuildHistoryText(toCompact, config.MaxCharsPerHistoryMessage);
                var clText         = ReadChangeLog();
                var hybridTrace    = ObservationExtractor.BuildToolTraceBlock(toCompact);
                var (summText, summUsage) = await GenerateSummaryAsync(
                    task, histText, clText, hybridTrace, toCompact.Count, cancellationToken);

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
                    Usage               = AccumulateCompactedUsage(toCompact, summUsage),
                    ToolCalls           = AccumulateCompactedToolCalls(toCompact),
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
                return (reconstructed with { Usage = AccumulateCompactedUsage(toCompact, null) }, toRetain);
            }
        }

        // LLM mode (default) — existing behaviour.
        if (mode is "lossless" or "intent")
            logger.LogWarning(
                "Compaction mode is '{Mode}' but no snapshotter or intent log is available — falling back to LLM mode.",
                mode);

        var historyText   = BuildHistoryText(toCompact, config.MaxCharsPerHistoryMessage);
        var changeLogText = ReadChangeLog();
        var toolTrace     = ObservationExtractor.BuildToolTraceBlock(toCompact);

        try
        {
            var (summaryText, summaryUsage) = await GenerateSummaryAsync(
                task, historyText, changeLogText, toolTrace, toCompact.Count, cancellationToken);

            var summary = new AgentMessage
            {
                AgentName           = "System",
                Content             = FormatSummaryContent(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, summaryText, prefixBlock),
                Role                = "user",
                TurnIndex           = toCompact[^1].TurnIndex,
                IsCompactionSummary = true,
                Usage               = AccumulateCompactedUsage(toCompact, summaryUsage),
                ToolCalls           = AccumulateCompactedToolCalls(toCompact),
            };

            logger.LogInformation(
                "Compaction complete. Turns 0–{Last} replaced by summary.",
                toCompact[^1].TurnIndex);

            return (PrependFallbackNotice(summary, intentFallbackNotice), toRetain);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLM compaction failed; inserting fallback marker for turns {First}–{Last}.",
                toCompact[0].TurnIndex, toCompact[^1].TurnIndex);
            var fallback = BuildFallbackSummary(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, ex.Message)
                with { ToolCalls = AccumulateCompactedToolCalls(toCompact) };
            return (PrependFallbackNotice(fallback, intentFallbackNotice), toRetain);
        }
    }

    // Internals

    // Collects all ToolCallRecord entries from the compacted turns into a flat list so the
    // summary message preserves them. Downstream consumers (telemetry, BuildModifiedFilesNote)
    // inspect ToolCalls on AgentMessages; without this they silently drop records for any turn
    // that was compacted, producing incomplete data for succeeded/failed tool tracking.
    private static IReadOnlyList<ToolCallRecord>? AccumulateCompactedToolCalls(
        IReadOnlyList<AgentMessage> compacted)
    {
        List<ToolCallRecord>? all = null;
        foreach (var m in compacted)
        {
            if (m.ToolCalls is not { Count: > 0 }) continue;
            all ??= [];
            all.AddRange(m.ToolCalls);
        }
        return all;
    }

    // Sums the token costs of all compacted turns and folds in the summary-call cost.
    // The total is stored on the summary AgentMessage so AgentOrchestrator can seed
    // cumulativeTokens correctly on the next StreamAsync call (after resume/compaction),
    // keeping MaxTotalTokens enforcement accurate across compaction boundaries.
    private static TokenUsage? AccumulateCompactedUsage(
        IReadOnlyList<AgentMessage> compacted,
        TokenUsage? summaryCallUsage)
    {
        int totalInput  = summaryCallUsage?.InputTokens  ?? 0;
        int totalOutput = summaryCallUsage?.OutputTokens ?? 0;
        foreach (var m in compacted)
        {
            if (m.Usage is null) continue;
            totalInput  += m.Usage.InputTokens;
            totalOutput += m.Usage.OutputTokens;
        }
        return (totalInput > 0 || totalOutput > 0)
            ? new TokenUsage(totalInput, totalOutput)
            : null;
    }

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

        if (ExpandedNote is not null)
            sb.Append("\n\n---\n" + ExpandedNote);

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
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Compaction: failed to read change log at '{Path}' — summary will proceed without it.",
                changeLogPath);
            return null;
        }
    }

    private async Task<(string Text, TokenUsage? Usage)> GenerateSummaryAsync(
        string task,
        string historyText,
        string? changeLogText,
        string? toolTraceText,
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

        // Tool trace: structured list of what each agent actually called (tool name + args +
        // success/fail). Gives the summariser ground-truth operation coverage even when the
        // raw tool results are truncated or absent from the conversation text.
        var toolTraceBlock = toolTraceText is not null
            ? $"\n\n{toolTraceText}\n\n"
            : string.Empty;

        var template = !string.IsNullOrWhiteSpace(config.SummaryTemplate)
            ? config.SummaryTemplate
            : SummaryPrompt;
        var prompt = template
            .Replace("{{$task}}",        task)
            .Replace("{{$turn_count}}",  turnCount.ToString())
            .Replace("{{$change_log}}", changeLogBlock + toolTraceBlock)
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

    private static AgentMessage PrependFallbackNotice(AgentMessage msg, string? notice) =>
        notice is null ? msg : msg with { Content = notice + "\n\n" + msg.Content };

    private AgentMessage BuildFallbackSummary(int firstTurn, int lastTurn, string errorMessage)
    {
        var content =
            $"[COMPACTION FAILED — covers turns {firstTurn + 1}–{lastTurn + 1}]\n\n" +
            $"Summary generation failed: {errorMessage}\n\n" +
            "Context for this turn range could not be preserved. Before acting:\n" +
            "• Read current file state directly — do not assume prior work was completed.\n" +
            "• Check the change log for ground truth of what was actually written.\n" +
            "• Re-derive your next step from observable disk state, not from memory.";

        if (ExpandedNote is not null)
            content += "\n\n---\n" + ExpandedNote;

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
        $"(1) read_file {FuseraftPaths.LocalBrief}, " +
        "(2) changes_read_latest to confirm what is already done, " +
        "(3) if an EXPLORATION HISTORY block appears above, use it — " +
        "those files were already investigated; jump directly to the candidate locations listed, " +
        "do not re-read files from scratch, " +
        "(4) do not redo work changes.json confirms is complete.";

    private string FormatSummaryContent(int firstTurn, int lastTurn, string summaryText, string prefixBlock = "")
    {
        var prefixSection = !string.IsNullOrEmpty(prefixBlock)
            ? prefixBlock + "\n\n---\n\n"
            : string.Empty;
        var header = $"{prefixSection}[CONVERSATION SUMMARY — covers turns {firstTurn + 1}–{lastTurn + 1}]\n\n{summaryText}";
        return ExpandedNote is not null
            ? $"{header}\n\n---\n{ExpandedNote}"
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
    private async Task<string> BuildObjectiveBlockAsync(CancellationToken ct)
    {
        if (objectiveManager is null) return string.Empty;
        try
        {
            var summary = await objectiveManager.BuildActiveSummaryAsync(ct);
            return summary ?? string.Empty;
        }
        catch { return string.Empty; }
    }

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
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Compaction: failed to read change log for symbol graph at '{Path}'.", changeLogPath);
            return [];
        }
    }

    // ---------------------------------------------------------------------------
    // Exploration block — derived automatically from observed tool-call behavior.
    // No model participation required: the framework already knows which files were
    // read, grepped, and searched. This survives compaction even when no code was
    // written, preserving investigation history that lossless reconstruction drops.
    // ---------------------------------------------------------------------------

    private async Task<string> BuildExplorationBlockAsync(CancellationToken ct)
    {
        if (!config.IncludeExploration) return string.Empty;
        if (eventsLogPath is null || _sessionId is not { Length: > 0 }) return string.Empty;

        var (fileReads, fileGreps) = await ParseToolCallEventsAsync();
        var shellPatterns          = await ExtractShellGrepPatternsAsync(ct);
        var fileSizes              = ReadFileSizesFromCache();

        if (fileReads.Count == 0 && fileGreps.Count == 0 && shellPatterns.Count == 0)
            return string.Empty;

        return BuildExplorationText(fileReads, fileGreps, shellPatterns, fileSizes);
    }

    // Scans events.jsonl for tool_call events in this session and counts read_file / grep_file calls.
    private async Task<(Dictionary<string, int> Reads, HashSet<string> Greps)> ParseToolCallEventsAsync()
    {
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var greps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(eventsLogPath)) return (reads, greps);
            foreach (var line in await File.ReadAllLinesAsync(eventsLogPath!))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("session", out var ses) || ses.GetString() != _sessionId) continue;
                    if (!root.TryGetProperty("event_type", out var et) || et.GetString() != "tool_call") continue;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("tool", out var toolEl)) continue;

                    var tool = toolEl.GetString() ?? string.Empty;
                    var arg  = payload.TryGetProperty("arg", out var argEl) ? argEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(arg)) continue;

                    if (tool.Equals("read_file", StringComparison.OrdinalIgnoreCase))
                        reads[arg] = reads.TryGetValue(arg, out var c) ? c + 1 : 1;
                    else if (tool.Equals("grep_file", StringComparison.OrdinalIgnoreCase))
                        greps.Add(arg);
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { /* best effort */ }
        return (reads, greps);
    }

    // Reads shell_run intent entries to extract grep/find command patterns performed this session.
    private async Task<List<string>> ExtractShellGrepPatternsAsync(CancellationToken ct)
    {
        if (intentLog is null) return [];
        try
        {
            var intents  = await intentLog.GetAllIntentsAsync(ct);
            var patterns = new List<string>();
            foreach (var intent in intents)
            {
                if (!string.Equals(intent.Operation.FunctionName, "shell_run", StringComparison.OrdinalIgnoreCase)) continue;
                var cmd = intent.Operation.ArgsSummary?.TryGetValue("command", out var v) == true
                    ? v?.ToString() : null;
                if (cmd is { Length: > 0 } &&
                    (cmd.Contains("grep", StringComparison.OrdinalIgnoreCase) ||
                     cmd.Contains("find", StringComparison.OrdinalIgnoreCase)))
                    patterns.Add(cmd.Length > 120 ? cmd[..120] + "…" : cmd);
            }
            return patterns;
        }
        catch { return []; }
    }

    // Reads file-size metadata from read_cache.json so the exploration block can annotate large files.
    private Dictionary<string, long> ReadFileSizesFromCache()
    {
        if (readCachePath is null || !File.Exists(readCachePath)) return [];
        try
        {
            using var doc  = JsonDocument.Parse(File.ReadAllText(readCachePath));
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes))
                    sizes[prop.Name] = bytes;
            return sizes;
        }
        catch { return []; }
    }

    private static string BuildExplorationText(
        Dictionary<string, int>     fileReads,
        HashSet<string>             fileGreps,
        List<string>                shellPatterns,
        Dictionary<string, long>    fileSizes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[EXPLORATION HISTORY — investigation performed before compaction]");
        sb.AppendLine();

        if (fileReads.Count > 0)
        {
            sb.AppendLine("Files read (read_file calls, most-accessed first):");
            foreach (var (path, count) in fileReads.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            {
                var shortPath = path.Contains('/') || path.Contains('\\')
                    ? path[(path.LastIndexOfAny(['/', '\\']) + 1)..]
                    : path;
                var display   = path.Length > 60 ? "…" + path[^57..] : path;
                var sizeNote  = fileSizes.TryGetValue(path, out var bytes) && bytes > 0
                    ? $"  ({bytes / 1024.0:F0} KB)" : string.Empty;
                sb.AppendLine($"  {display,-62} ×{count}{sizeNote}");
            }
            sb.AppendLine();
        }

        // Grepped files (deduped with reads: only list files NOT already in the reads list)
        var grepsOnly = fileGreps.Where(f => !fileReads.ContainsKey(f)).ToList();
        if (grepsOnly.Count > 0)
        {
            sb.AppendLine("Files grepped (grep_file calls, not already listed above):");
            foreach (var path in grepsOnly.OrderBy(p => p))
            {
                var display = path.Length > 60 ? "…" + path[^57..] : path;
                sb.AppendLine($"  {display}");
            }
            sb.AppendLine();
        }

        if (shellPatterns.Count > 0)
        {
            sb.AppendLine("Shell searches performed:");
            foreach (var cmd in shellPatterns)
                sb.AppendLine($"  {cmd}");
            sb.AppendLine();
        }

        // Inferred candidates: files read ≥3 times (excluding artifact files)
        var candidates = fileReads
            .Where(kv => kv.Value >= 3 && !kv.Key.Contains(".fuseraft"))
            .OrderByDescending(kv => kv.Value)
            .ToList();
        if (candidates.Count > 0)
        {
            sb.AppendLine("Inferred candidate locations (read ≥3 times — likely relevant):");
            foreach (var (path, count) in candidates)
            {
                var display = path.Length > 60 ? "…" + path[^57..] : path;
                sb.AppendLine($"  {display}  (read {count}×)");
            }
            sb.AppendLine();
        }

        sb.Append("Do not re-read these files from scratch. " +
                  "Jump directly to specific regions, or proceed to implementation.");
        return sb.ToString().TrimEnd();
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
