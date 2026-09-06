using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Context;

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
    fuseraft.Infrastructure.Objectives.ObjectiveManager? objectiveManager = null,
    fuseraft.Infrastructure.Knowledge.KnowledgeSnapshotEnricher? knowledgeEnricher = null,
    string? readCachePath = null,
    string? executionStatePath = null,
    string? briefPath = null)
{
    // Tracks savings ratios from the last AntiThrashWindow compactions so we can detect
    // conversations that are thrashing (repeatedly compacting but saving very little).
    private readonly Queue<double> _recentSavings = new();
    private string _sessionId = string.Empty;

    private readonly CompactionPrefixBlockBuilder _prefixBlocks = new(
        config, logger, changeLogPath, intentLog, eventsLogPath, evidenceStore,
        objectiveManager, readCachePath, briefPath);

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>Exposes the compaction configuration for callers that need to inspect it.</summary>
    public CompactionConfig Config => config;

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
        (config.Mode ?? CompactionModes.Llm).Equals(CompactionModes.Window, StringComparison.OrdinalIgnoreCase);

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
            // Use the same TokenEstimator ratio as TrimToWindow so the trigger and the trim
            // measure the same quantity. Usage.TotalTokens is the cumulative API call cost
            // (InputTokens = full context at that turn, not just this message), so summing
            // it across messages grows quadratically and diverges from the char-based budget
            // that TokenBudget is calibrated against — causing the trigger to fire while
            // TrimToWindow finds nothing to drop.
            var estimated = messages.Sum(m => TokenEstimator.EstimateTokens(m.Content?.Length ?? 0));
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
        var assistantTurns = messages.Count(m => m.Role == MessageRole.Assistant);
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
        var total = list.Sum(m => TokenEstimator.EstimateTokens(m.Content?.Length ?? 0));
        if (total <= config.TokenBudget) return list;

        // Skip pinned messages (compaction summaries) — they're already compact and
        // losing them would discard context that can't be recovered.
        int start = 0;
        while (start < list.Count && list[start].IsCompactionSummary) start++;

        while (total > config.TokenBudget && start + 1 < list.Count)
        {
            if (list[start].Role == MessageRole.User)
            {
                total -= TokenEstimator.EstimateTokens(list[start].Content?.Length ?? 0);
                list.RemoveAt(start);
            }
            if (start + 1 < list.Count && list[start].Role == MessageRole.Assistant)
            {
                total -= TokenEstimator.EstimateTokens(list[start].Content?.Length ?? 0);
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
            var passthrough = messages.Count == 1 ? messages[0] : new AgentMessage { Role = "user", Content = "(empty session)", AgentName = AgentNames.System };
            return (passthrough, []);
        }

        var keepCount  = Math.Clamp(config.KeepRecentTurns, 1, messages.Count - 1);
        var toCompact  = messages.Take(messages.Count - keepCount).ToList();
        var toRetain   = messages.Skip(messages.Count - keepCount).ToList();

        // Record savings ratio now so it's captured regardless of which compaction path we take.
        // toCompact.Count messages become 1 summary; net reduction = toCompact.Count - 1.
        RecordSavings((toCompact.Count - 1.0) / messages.Count);

        logger.LogDebug(
            "Compacting {Compacted} turns (0–{LastCompacted}) into a summary; retaining {Kept} recent turns.",
            toCompact.Count, toCompact[^1].TurnIndex, toRetain.Count);

        var mode = (config.Mode ?? CompactionModes.Llm).ToLowerInvariant();

        var prefixBlock = await _prefixBlocks.BuildAsync(
            toCompact[0].TurnIndex, toCompact[^1].TurnIndex, _sessionId, cancellationToken);

        // Phase 3: load ExecutionState once here so both LLM and hybrid paths can use it
        // for content filtering and prompt addendum without re-reading the file.
        var executionState    = await TryLoadExecutionStateAsync(cancellationToken);
        var filteredCompact   = FilterForCompaction(toCompact, executionState);
        var executionStateNote = executionState is not null ? ExecutionStateCompactionNote : null;

        // Intent mode: reconstruct from the intent log — fully deterministic, no LLM call.
        // When the intent log is unavailable, record a visible fallback notice so agents
        // resuming after compaction know the summary was degraded.
        string? intentFallbackNotice = null;
        if (mode == CompactionModes.Intent)
        {
            if (intentLog is not null)
                return await CompactFromIntentAsync(toCompact, toRetain, prefixBlock, cancellationToken);

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
        if ((mode == CompactionModes.Lossless || mode == CompactionModes.Intent) && snapshotter is not null)
            return await CompactLosslessAsync(toCompact, toRetain, snapshotter, prefixBlock, intentFallbackNotice, cancellationToken);

        // Hybrid: prepend reconstruction before the LLM summary.
        if (mode == CompactionModes.Hybrid && snapshotter is not null)
            return await CompactHybridAsync(task, toCompact, toRetain, snapshotter, prefixBlock, filteredCompact, executionStateNote, cancellationToken);

        // LLM mode (default) — existing behaviour.
        if (mode is CompactionModes.Lossless or CompactionModes.Intent)
            logger.LogWarning(
                "Compaction mode is '{Mode}' but no snapshotter or intent log is available — falling back to LLM mode.",
                mode);

        return await CompactWithLlmAsync(task, toCompact, toRetain, prefixBlock, filteredCompact, executionStateNote, intentFallbackNotice, cancellationToken);
    }

    // Intent-log-derived summary path: fully deterministic, no LLM call.
    private async Task<(AgentMessage Summary, IReadOnlyList<AgentMessage> Retained)> CompactFromIntentAsync(
        List<AgentMessage> toCompact,
        List<AgentMessage> toRetain,
        string prefixBlock,
        CancellationToken cancellationToken)
    {
        var intents = await intentLog!.GetIntentsForRangeAsync(
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

    // Evidence snapshot reconstruction path: skips LLM call entirely; rebuilds from durable state.
    private async Task<(AgentMessage Summary, IReadOnlyList<AgentMessage> Retained)> CompactLosslessAsync(
        List<AgentMessage> toCompact,
        List<AgentMessage> toRetain,
        IContextSnapshotter snapshotter,
        string prefixBlock,
        string? intentFallbackNotice,
        CancellationToken cancellationToken)
    {
        var snapshot      = await EnrichWithKnowledgeAsync(await snapshotter.SnapshotAsync(cancellationToken), cancellationToken);
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
        logger.LogDebug(
            "Lossless compaction: {Compacted} turns replaced by evidence reconstruction.",
            toCompact.Count);
        return (PrependFallbackNotice(reconstructed, intentFallbackNotice), toRetain);
    }

    // Hybrid reconstruction + LLM path: prepends evidence reconstruction before the LLM summary.
    private async Task<(AgentMessage Summary, IReadOnlyList<AgentMessage> Retained)> CompactHybridAsync(
        string task,
        List<AgentMessage> toCompact,
        List<AgentMessage> toRetain,
        IContextSnapshotter snapshotter,
        string prefixBlock,
        IReadOnlyList<AgentMessage> filteredCompact,
        string? executionStateNote,
        CancellationToken cancellationToken)
    {
        var snapshot      = await EnrichWithKnowledgeAsync(await snapshotter.SnapshotAsync(cancellationToken), cancellationToken);
        var reconstructed = ContextRebuilder.BuildContextMessage(snapshot, toCompact[^1].TurnIndex);

        try
        {
            var histText       = BuildHistoryText(filteredCompact, config.MaxCharsPerHistoryMessage);
            var clText         = ReadChangeLog();
            var hybridTrace    = ObservationExtractor.BuildToolTraceBlock(toCompact);
            var (summText, summUsage) = await GenerateSummaryAsync(
                task, histText, clText, hybridTrace, toCompact.Count, cancellationToken, executionStateNote);

            var hybridContent =
                reconstructed.Content + "\n\n---\n\n" +
                FormatSummaryContent(toCompact[0].TurnIndex, toCompact[^1].TurnIndex, summText, prefixBlock);

            var hybridSummary = new AgentMessage
            {
                AgentName           = AgentNames.System,
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

    // Pure LLM compaction path (default).
    private async Task<(AgentMessage Summary, IReadOnlyList<AgentMessage> Retained)> CompactWithLlmAsync(
        string task,
        List<AgentMessage> toCompact,
        List<AgentMessage> toRetain,
        string prefixBlock,
        IReadOnlyList<AgentMessage> filteredCompact,
        string? executionStateNote,
        string? intentFallbackNotice,
        CancellationToken cancellationToken)
    {
        var historyText   = BuildHistoryText(filteredCompact, config.MaxCharsPerHistoryMessage);
        var changeLogText = ReadChangeLog();
        var toolTrace     = ObservationExtractor.BuildToolTraceBlock(toCompact);

        try
        {
            var (summaryText, summaryUsage) = await GenerateSummaryAsync(
                task, historyText, changeLogText, toolTrace, toCompact.Count, cancellationToken, executionStateNote);

            var summary = new AgentMessage
            {
                AgentName           = AgentNames.System,
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

    // Knowledge snapshot enrichment: applies knowledgeEnricher to a snapshot when available.
    private async Task<ContextSnapshot> EnrichWithKnowledgeAsync(
        ContextSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (knowledgeEnricher is not null)
            snapshot = await knowledgeEnricher.EnrichAsync(snapshot, cancellationToken);
        return snapshot;
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
        IReadOnlyList<IntentEntry> intents,
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
                var icon   = intent.Status == IntentStatus.Applied ? "✓"
                           : intent.Status == IntentStatus.Failed  ? "✗"
                           : "⧖"; // hourglass for pending/retryable
                var target = intent.Operation.TargetPath is { } p ? $" → \"{p}\"" : string.Empty;
                var detail = intent.Status == IntentStatus.Failed && intent.ErrorMessage is { } err
                    ? $" — {err}"
                    : string.Empty;

                sb.AppendLine(
                    $"  {icon} {intent.Operation.FunctionName}{target}" +
                    $" (turn {intent.TurnIndex + 1}, {intent.Agent}){detail}");
            }
        }

        var pending = intents.Count(e => e.Status == IntentStatus.Pending);
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
            AgentName           = AgentNames.System,
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
        CancellationToken cancellationToken,
        string? executionStateNote = null)
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

        var executionStateBlock = executionStateNote is not null
            ? $"\n\n{executionStateNote}\n\n"
            : string.Empty;

        var template = !string.IsNullOrWhiteSpace(config.SummaryTemplate)
            ? config.SummaryTemplate
            : SummaryPrompt;
        var prompt = template
            .Replace("{{$task}}",        task)
            .Replace("{{$turn_count}}",  turnCount.ToString())
            .Replace("{{$change_log}}", changeLogBlock + toolTraceBlock + executionStateBlock)
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
                : $"[{(msg.Role == MessageRole.User ? AgentNames.Human : msg.AgentName)} — Turn {msg.TurnIndex + 1}]";

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
            AgentName           = AgentNames.System,
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
        $"(1) if the summary above does not already show the goal and files_to_change from {FuseraftPaths.LocalBrief}, read_file it now — otherwise use what is in the summary, " +
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

    private static readonly JsonSerializerOptions ChangeLogJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---------------------------------------------------------------------------
    // Phase 3 — Execution-state-aware compaction filter
    // ---------------------------------------------------------------------------

    private const string ExecutionStateCompactionNote =
        "EXECUTION STATE NOTE: The current ExecutionState (injected separately into every " +
        "agent turn) already records: build pass/fail status and compiler errors, failed " +
        "attempt history, and open tasks. Do NOT summarize this information. Focus the " +
        "summary on: decisions made and their rationale, architectural constraints " +
        "discovered, agent coordination and handoffs, and information NOT captured in ExecutionState.";

    private async Task<ExecutionState?> TryLoadExecutionStateAsync(CancellationToken ct)
    {
        if (executionStatePath is null || !File.Exists(executionStatePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(executionStatePath, ct);
            return JsonSerializer.Deserialize<ExecutionState>(json, ChangeLogJsonOpts);
        }
        catch { return null; }
    }

    // Returns a copy of the message list with verbose content replaced by short markers
    // for entries whose information is already captured in ExecutionState. Only Content
    // is modified — ToolCalls is preserved so the tool-trace block remains accurate.
    private static IReadOnlyList<AgentMessage> FilterForCompaction(
        IReadOnlyList<AgentMessage> messages,
        ExecutionState? state)
    {
        if (state is null) return messages;

        var capturedPaths = state.SignificantChanges
            .Select(c => c.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<AgentMessage>(messages.Count);
        foreach (var msg in messages)
            result.Add(ApplyCompactionMessageFilter(msg, capturedPaths));
        return result;
    }

    private static AgentMessage ApplyCompactionMessageFilter(
        AgentMessage msg, HashSet<string> capturedPaths)
    {
        if (msg.ToolCalls is not { Count: > 0 }) return msg;

        // Build commands: shell_run with a build/publish/test command → already in ExecutionState.Build.
        if (msg.ToolCalls.Any(tc => IsShellRunCall(tc.Name) && IsBuildCommand(tc.ArgsSummary)))
            return msg with { Content = "[shell_run output captured in ExecutionState]" };

        // File operations: all write/patch/delete calls where every touched path is already
        // logged in ExecutionState.SignificantChanges → content adds no new information.
        var fileOps = msg.ToolCalls.Where(tc => IsFileOpCall(tc.Name)).ToList();
        if (fileOps.Count > 0 && fileOps.All(tc => IsPathCaptured(tc.ArgsSummary, capturedPaths)))
            return msg with { Content = "[file operations logged in ExecutionState]" };

        return msg;
    }

    private static bool IsShellRunCall(string name) =>
        name.Replace("_", "").Equals("shellrun", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildCommand(string? argsSummary)
    {
        if (argsSummary is null) return false;
        var lower = argsSummary.ToLowerInvariant();
        return lower.Contains("build")   || lower.Contains("publish") ||
               lower.Contains("compile") || lower.Contains("cargo")   ||
               lower.Contains("pytest")  || lower.Contains("cmake")   ||
               lower.Contains("npm run") || lower.Contains("go test");
    }

    private static bool IsFileOpCall(string name)
    {
        var n = name.Replace("_", "").ToLowerInvariant();
        return n is "writefile" or "patchfile" or "deletefile";
    }

    // ArgsSummary for write_file/patch_file is "path=<value>" (up to 60 chars, may be truncated).
    // Checks whether the path referenced by the summary appears in the captured-paths set.
    private static bool IsPathCaptured(string? argsSummary, HashSet<string> capturedPaths)
    {
        if (argsSummary is null) return false;
        const string key = "path=";
        var idx = argsSummary.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var partial = argsSummary[(idx + key.Length)..].TrimEnd('.', ' ');
        if (partial.Length == 0) return false;
        return capturedPaths.Any(p =>
            p.EndsWith(partial, StringComparison.OrdinalIgnoreCase) ||
            p.Contains(partial, StringComparison.OrdinalIgnoreCase));
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
