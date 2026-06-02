using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Applies a <see cref="ContextWindowConfig"/> to a conversation history, returning a
/// filtered slice suitable for passing to an agent's <c>RunAsync</c> call.
///
/// Filters are applied in this order:
/// <list type="number">
///   <item><see cref="ContextWindowConfig.TextOnly"/> / <see cref="ContextWindowConfig.ExcludeAgents"/>
///     — strip tool messages and/or specific agents' output.</item>
///   <item><see cref="ContextWindowConfig.MaxTailMessages"/> — keep only the last N messages.</item>
/// </list>
///
/// The original history list is never mutated.
/// </summary>
public static class ContextWindowFilter
{
    /// <summary>
    /// Returns a filtered view of <paramref name="history"/> according to
    /// <paramref name="window"/>. Returns <paramref name="history"/> unchanged
    /// when <paramref name="window"/> is <c>null</c>.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Apply(
        IEnumerable<ChatMessage> history,
        ContextWindowConfig? window)
    {
        if (window is null) return history.ToList();

        IEnumerable<ChatMessage> messages = history;

        // Step 1: Strip tool messages.
        //
        // Triggered by TextOnly OR a non-empty ExcludeAgents list.
        // When ExcludeAgents is set we must also strip ChatRole.Tool result messages
        // even when TextOnly is false, because tool results are not attributed to a
        // specific agent. Leaving them in after stripping the corresponding call frames
        // would produce a malformed context with orphaned results.
        //
        // Mixed assistant messages (text + tool-call content in the same message) are
        // reduced to their text-only contents rather than kept as-is. Keeping the full
        // message while stripping the corresponding ChatRole.Tool result would leave
        // orphaned tool_use ids and cause a 400 from strict providers (e.g. Bedrock).
        if (window.TextOnly || window.ExcludeAgents.Count > 0)
        {
            var filtered = new List<ChatMessage>();
            foreach (var m in messages)
            {
                if (m.Role == ChatRole.User)
                {
                    filtered.Add(m);
                    continue;
                }

                if (m.Role != ChatRole.Assistant)
                    continue; // drop ChatRole.Tool result messages

                var textContents = m.Contents
                    .OfType<TextContent>()
                    .Where(t => !string.IsNullOrEmpty(t.Text))
                    .ToList<AIContent>();

                if (textContents.Count == 0)
                    continue; // pure tool-call frame (or empty-text frame) — drop

                var hasToolCalls = m.Contents.OfType<FunctionCallContent>().Any();
                if (!hasToolCalls)
                {
                    filtered.Add(m); // already text-only — keep as-is
                    continue;
                }

                // Mixed message: strip tool-call content, keep only text.
                // This prevents orphaned tool_use ids when the corresponding
                // ChatRole.Tool result messages are not included in the slice.
                filtered.Add(new ChatMessage(ChatRole.Assistant, textContents) { AuthorName = m.AuthorName });
            }
            messages = filtered;
        }

        // Step 2: Exclude messages authored by listed agents.
        // Both text-bearing and (after step 1) any remaining assistant messages
        // authored by the excluded agents are removed.
        if (window.ExcludeAgents.Count > 0)
        {
            messages = messages.Where(m =>
                m.Role != ChatRole.Assistant ||
                !window.ExcludeAgents.Contains(
                    m.AuthorName ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase));
        }

        // Step 3: Turn-age limit — keep only messages from the last N agent turns.
        // An "agent turn" is the span ending at each assistant message. We walk backward
        // counting assistant messages; the first index where the count equals MaxTurnAge
        // becomes the cut-point so that only the last N turns survive.
        var list = messages.ToList();

        if (window.MaxTurnAge > 0 && list.Count > 0)
        {
            int assistantTurnsSeen = 0;
            int cutIndex = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Role == ChatRole.Assistant)
                    assistantTurnsSeen++;
                if (assistantTurnsSeen >= window.MaxTurnAge)
                {
                    cutIndex = i;
                    break;
                }
            }
            // Only trim when we actually found enough turns; otherwise keep everything.
            if (assistantTurnsSeen >= window.MaxTurnAge && cutIndex > 0)
                list = list.Skip(cutIndex).ToList();
        }

        // Step 4: Tail limit — keep only the last N messages.
        // Correction messages (RETRY, STAGNATION, [fuseraft:blocked, etc.) are pinned so they
        // always survive the position-based cut. Non-correction messages are trimmed to the tail
        // window; the final list preserves original message order.
        if (window.MaxTailMessages > 0 && list.Count > window.MaxTailMessages)
        {
            var pinnedSet = new HashSet<int>(
                Enumerable.Range(0, list.Count).Where(i => IsCorrectionMessage(list[i])));

            if (pinnedSet.Count == 0)
            {
                list = list.Skip(list.Count - window.MaxTailMessages).ToList();
            }
            else
            {
                var unpinnedIndices = Enumerable.Range(0, list.Count)
                    .Where(i => !pinnedSet.Contains(i))
                    .ToList();

                int firstKeptUnpinned = unpinnedIndices.Count > window.MaxTailMessages
                    ? unpinnedIndices[unpinnedIndices.Count - window.MaxTailMessages]
                    : 0;

                var kept = new List<ChatMessage>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    if (i >= firstKeptUnpinned || pinnedSet.Contains(i))
                        kept.Add(list[i]);
                }
                list = kept;
            }
        }

        // Step 5: Sanitize tool_use/tool_result pairing at slice boundaries.
        // Steps 3 and 4 cut by position; either cut can land inside a tool-call/result
        // sequence, producing an assistant message whose FunctionCallContent IDs have no
        // matching ChatRole.Tool results in the retained slice. Strict providers (Bedrock)
        // reject such messages with a 400. Strip orphaned tool calls to text-only here so
        // the slice is always well-formed regardless of where the cut landed.
        list = SanitizeToolPairs(list);

        // Step 6: Truncate large tool results.
        // Tool outputs from prior turns (file reads, shell output, search results) are
        // replayed verbatim on every subsequent agent call, compounding context growth.
        // When MaxToolResultChars is set, any FunctionResultContent string that exceeds
        // the limit is truncated and annotated with the omitted character count.
        if (window.MaxToolResultChars > 0)
            list = TruncateToolResults(list, window.MaxToolResultChars, window.ToolResultCharOverrides);

        // Step 7: Truncate verbose assistant messages.
        // When MaxReplayChars is set, assistant text content that exceeds the limit is
        // truncated. Compaction-summary messages (marked by their header prefix) are exempt.
        if (window.MaxReplayChars > 0)
            list = TruncateAssistantContent(list, window.MaxReplayChars);

        return list;
    }

    private static List<ChatMessage> TruncateAssistantContent(List<ChatMessage> list, int maxChars)
    {
        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant)
            {
                result.Add(msg);
                continue;
            }

            var textContent = string.Concat(msg.Contents.OfType<TextContent>().Select(t => t.Text));
            // Compaction summaries are already compact — skip them unconditionally.
            if (textContent.StartsWith("[CONVERSATION SUMMARY", StringComparison.Ordinal) ||
                textContent.Length <= maxChars)
            {
                result.Add(msg);
                continue;
            }

            var truncated = textContent[..maxChars] +
                $"\n[...truncated — {textContent.Length - maxChars:N0} chars omitted to reduce context size...]";

            var newContents = msg.Contents
                .Where(c => c is not TextContent)
                .Prepend(new TextContent(truncated))
                .ToList<AIContent>();

            result.Add(new ChatMessage(ChatRole.Assistant, newContents) { AuthorName = msg.AuthorName });
        }
        return result;
    }

    // How much of a consumed read_file result to keep for structural context (file shape,
    // imports, class header) after a downstream write/patch confirms the content was acted on.
    // The rest is elided — the model's mental model of the file is stale at that point anyway.
    private const int ConsumedReadCapChars = 500;

    private static List<ChatMessage> TruncateToolResults(
        List<ChatMessage> list,
        int maxChars,
        IReadOnlyDictionary<string, int>? overrides = null)
    {
        // Fast path: no ChatRole.Tool messages in the slice.
        if (!list.Any(m => m.Role == ChatRole.Tool)) return list;

        // Build the set of read_file call IDs that have a downstream write/patch to the same
        // path. Those results are stale and can be aggressively capped; unconsumed reads that
        // the model hasn't yet acted on are left at the normal maxChars limit.
        var consumedReadIds = BuildConsumedReadCallIds(list);

        // Build callId → toolName so per-tool overrides can be resolved for each result.
        var callToolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var c in msg.Contents)
                if (c is FunctionCallContent fc && fc.CallId is not null)
                    callToolNames[fc.CallId] = fc.Name ?? string.Empty;
        }

        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Tool)
            {
                result.Add(msg);
                continue;
            }

            bool anyTruncated = false;
            var newContents = new List<AIContent>(msg.Contents.Count);
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent fr && fr.Result is string s)
                {
                    string? truncated = null;

                    if (consumedReadIds.Contains(fr.CallId ?? string.Empty) &&
                        s.Length > ConsumedReadCapChars)
                    {
                        // Consumed read: a downstream write/patch to this file exists, so the
                        // content is stale. Keep a small structural preview and elide the rest.
                        truncated = s[..ConsumedReadCapChars] +
                            $"\n[...{s.Length - ConsumedReadCapChars:N0} chars elided — " +
                            $"file was written or patched later this session; " +
                            $"call read_file again if current content is needed]";
                    }
                    else
                    {
                        // Resolve the per-tool limit: check overrides first, then fall back to maxChars.
                        // A zero override value disables truncation for that tool entirely.
                        int limit = maxChars;
                        if (overrides is { Count: > 0 } &&
                            callToolNames.TryGetValue(fr.CallId ?? string.Empty, out var toolName))
                        {
                            foreach (var kv in overrides)
                            {
                                if (string.Equals(kv.Key, toolName, StringComparison.OrdinalIgnoreCase))
                                {
                                    limit = kv.Value;
                                    break;
                                }
                            }
                        }

                        if (limit > 0 && s.Length > limit)
                        {
                            truncated = s[..limit] +
                                $"\n[...truncated — {s.Length - limit:N0} chars omitted to reduce context size...]";
                        }
                    }

                    if (truncated is not null)
                    {
                        newContents.Add(new FunctionResultContent(fr.CallId!, truncated));
                        anyTruncated = true;
                    }
                    else
                    {
                        newContents.Add(content);
                    }
                }
                else
                {
                    newContents.Add(content);
                }
            }

            result.Add(anyTruncated
                ? new ChatMessage(ChatRole.Tool, newContents)
                : msg);
        }
        return result;
    }

    /// <summary>
    /// Scans <paramref name="messages"/> for <c>read_file</c> calls and returns the set of
    /// call IDs whose file was subsequently written or patched. These results are stale and
    /// can be aggressively capped during context trimming without harming accuracy.
    /// </summary>
    internal static HashSet<string> BuildConsumedReadCallIds(IReadOnlyList<ChatMessage> messages)
    {
        // Collect all function calls in message order: (callId, name, path, messageIndex).
        var calls = new List<(string CallId, string Name, string? Path, int MsgIdx)>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var content in msg.Contents)
            {
                if (content is not FunctionCallContent fc) continue;
                var path = ExtractPathArg(fc.Arguments);
                calls.Add((fc.CallId ?? fc.Name ?? string.Empty, fc.Name ?? string.Empty, path, i));
            }
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, name, path, msgIdx) in calls)
        {
            if (!IsReadFile(name) || path is null) continue;

            // Mark as consumed when any later write_file or patch_file targets the same path.
            bool hasDownstreamWrite = calls.Any(c =>
                c.MsgIdx > msgIdx &&
                IsWriteOrPatchFile(c.Name) &&
                string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

            if (hasDownstreamWrite)
                consumed.Add(callId);
        }
        return consumed;
    }

    private static string? ExtractPathArg(IDictionary<string, object?>? args)
    {
        if (args is null) return null;
        foreach (var kv in args)
        {
            if (string.Equals(kv.Key, "path", StringComparison.OrdinalIgnoreCase))
                return kv.Value?.ToString();
        }
        return null;
    }

    private static bool IsReadFile(string name) =>
        string.Equals(name, "read_file", StringComparison.OrdinalIgnoreCase);

    private static bool IsWriteOrPatchFile(string name) =>
        string.Equals(name, "write_file",  StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "patch_file",  StringComparison.OrdinalIgnoreCase);

    // Removes tool-pairing violations that arise after positional slice cuts:
    //   • Leading ChatRole.Tool messages with no preceding assistant tool-call are dropped.
    //   • Assistant messages whose FunctionCallContent IDs are not fully covered by the
    //     immediately following ChatRole.Tool messages are reduced to text-only (or dropped
    //     entirely when they have no text content either).
    private static List<ChatMessage> SanitizeToolPairs(List<ChatMessage> list)
    {
        // Fast path: if no assistant message has any tool calls, nothing to fix.
        if (!list.Any(m => m.Role == ChatRole.Assistant &&
                           m.Contents.OfType<FunctionCallContent>().Any()))
            return list;

        var result = new List<ChatMessage>(list.Count);
        int i = 0;
        while (i < list.Count)
        {
            var msg = list[i];

            // Drop orphaned tool-result messages at the head of the slice or wherever
            // they appear without a preceding assistant call in the result list.
            if (msg.Role == ChatRole.Tool)
            {
                bool hasPrecedingCall = result.Count > 0 &&
                    result[^1].Role == ChatRole.Assistant &&
                    result[^1].Contents.OfType<FunctionCallContent>().Any();
                if (!hasPrecedingCall) { i++; continue; }
                result.Add(msg);
                i++;
                continue;
            }

            if (msg.Role == ChatRole.Assistant)
            {
                var toolCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
                if (toolCalls.Count > 0)
                {
                    // Collect the call IDs this message expects to be answered.
                    var expectedIds = toolCalls
                        .Select(tc => tc.CallId)
                        .Where(id => id is not null)
                        .ToHashSet();

                    // Scan the immediately following ChatRole.Tool messages for results.
                    var coveredIds = new HashSet<string?>();
                    for (int j = i + 1; j < list.Count && list[j].Role == ChatRole.Tool; j++)
                    {
                        foreach (var fr in list[j].Contents.OfType<FunctionResultContent>())
                            coveredIds.Add(fr.CallId);
                    }

                    // If any call is uncovered, reduce this message to text-only.
                    if (!expectedIds.All(id => coveredIds.Contains(id)))
                    {
                        var textContents = msg.Contents
                            .OfType<TextContent>()
                            .Where(t => !string.IsNullOrEmpty(t.Text))
                            .ToList<AIContent>();

                        if (textContents.Count > 0)
                            result.Add(new ChatMessage(ChatRole.Assistant, textContents)
                                { AuthorName = msg.AuthorName });
                        // Drop entirely when there is no text — a pure tool-call frame
                        // without its results adds no value to the context.
                        i++;
                        continue;
                    }
                }
            }

            result.Add(msg);
            i++;
        }
        return result;
    }

    // Prefixes that unambiguously identify a ChatRole.User correction injected by
    // CorrectionEngine, routing strategies, or the orchestrator's verifier hook.
    private static readonly string[] CorrectionPrefixes =
    [
        "RETRY ",
        "NO TOOL CALLS",
        "CRITICAL:",
        "APPROVED rejected:",
        "WRONG KEYWORD:",
        "JSON block correct",
        "BUILD FAILURE:",
        "STAGNATION (",
        "STUCK ",
        "HALLUCINATION:",
        "PERSISTENT BUILD FAILURE",
        "VERIFICATION FINDING",
        "Files written this turn",
        "No handoff keyword",
        "EVIDENCE INCONSISTENCY",   // ConflictingEvidence (KeywordSelectionStrategy)
        "EVIDENCE AUDIT REQUIRED",  // ConflictingEvidence (StateMachineSelectionStrategy)
        "MISSING ARTIFACT",         // MissingEvidence (both strategies)
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="message"/> is a correction injected by
    /// <see cref="fuseraft.Orchestration.Workflow.CorrectionEngine"/>, a routing strategy,
    /// or the orchestrator's verifier hook. Used to pin corrections so they survive
    /// <see cref="ContextWindowConfig.MaxTailMessages"/> trimming, and to re-inject them
    /// into assembled agent contexts.
    /// </summary>
    public static bool IsCorrectionMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.User) return false;
        var text = message.Text ?? string.Empty;
        if (text.Contains("[fuseraft:blocked", StringComparison.Ordinal)) return true;
        foreach (var prefix in CorrectionPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    // Global default applied during checkpoint-resume replay when no per-agent limit is set.
    // Agents sometimes produce verbose stream-of-consciousness reasoning text (3–5k output
    // tokens). When that text is replayed verbatim in every subsequent turn it causes
    // compaction summaries to grow each cycle and in-turn input tokens to balloon (450k+).
    // Compaction summaries (IsCompactionSummary) are already compact and are never truncated.
    internal const int DefaultMaxReplayChars = 2_000;

    /// <summary>
    /// Returns the content string to replay for <paramref name="message"/> into the next
    /// <c>StreamAsync</c> call's history. Verbose non-summary assistant messages are
    /// truncated at <paramref name="maxReplayChars"/> to prevent compounding context growth.
    /// </summary>
    public static string TruncateReplayContent(AgentMessage message, int maxReplayChars = DefaultMaxReplayChars)
    {
        var content = message.Content ?? string.Empty;

        if (message.IsCompactionSummary
            || message.Role != "assistant"
            || content.Length <= maxReplayChars)
            return content;

        return content[..maxReplayChars] +
               $"\n[...truncated — {content.Length - maxReplayChars:N0} chars omitted to reduce context size...]";
    }
}
