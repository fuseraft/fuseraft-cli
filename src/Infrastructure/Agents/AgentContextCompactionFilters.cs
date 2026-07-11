using System.Collections.Concurrent;
using System.Text;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// In-turn message-compaction/dedup filter library: truncates verbose intermediate reasoning,
/// drops or compresses superseded tool-call/result pairs (writes, observational reads, shell
/// runs), caps the sliding tool-pair window, and trims by char budget. Extracted from
/// <see cref="AgentFactory"/> — every method here was already <c>internal static</c> with
/// explicit parameters and no instance-state dependency (aside from the static
/// <see cref="_toolPairStrategies"/> cache, which moved with <see cref="KeepLastToolPairs"/>),
/// and is independently consumed by <c>src/Cli/Commands/Repl/ReplFactory.cs</c> — this file
/// just gives that existing quasi-public surface an honest home.
/// </summary>
internal static class AgentContextCompactionFilters
{
    // Maximum chars kept for text/reasoning content in an intermediate tool-calling message.
    private const int MaxIntermediateAssistantTextChars = 120;
    // Maximum chars kept for a single function-call argument value in an intermediate message.
    // Large values (e.g. write_file content argument) accumulate in every subsequent step's
    // call frame, causing O(N) growth per step that compounds across N steps to O(N²) total.
    private const int MaxIntermediateArgValueChars = 500;

    /// <summary>
    /// Truncates verbose content in intermediate (tool-calling) assistant messages:
    /// <list type="bullet">
    ///   <item>Text and reasoning content truncated to <see cref="MaxIntermediateAssistantTextChars"/>.
    ///     <see cref="TextReasoningContent.ProtectedData"/> is preserved so the provider can
    ///     continue the reasoning chain.</item>
    ///   <item>Large <see cref="FunctionCallContent"/> argument values truncated to
    ///     <see cref="MaxIntermediateArgValueChars"/>. Short values (paths, flags) are kept
    ///     in full; only bulk payloads (file contents, scripts) are elided.</item>
    /// </list>
    /// Pure-text (non-tool) messages are never modified.
    /// </summary>
    internal static IEnumerable<ChatMessage> TruncateIntermediateAssistantReasoning(
        IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Fast path: no assistant messages with tool calls.
        if (!list.Any(m => m.Role == ChatRole.Assistant &&
                           m.Contents.OfType<FunctionCallContent>().Any()))
            return list;

        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant)
            {
                result.Add(msg);
                continue;
            }

            if (!msg.Contents.OfType<FunctionCallContent>().Any())
            {
                // Pure text message (final response, orchestrator signal) — keep as-is.
                result.Add(msg);
                continue;
            }

            // Intermediate tool-calling message: truncate each content item individually.
            bool anyTruncated = false;
            var rebuilt = new List<AIContent>(msg.Contents.Count);
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent trc:
                        // Truncate verbose reasoning text. ProtectedData (the opaque blob the
                        // provider needs for round-trip extended thinking) is preserved intact.
                        if (!string.IsNullOrEmpty(trc.Text) && trc.Text.Length > MaxIntermediateAssistantTextChars)
                        {
                            rebuilt.Add(new TextReasoningContent(
                                trc.Text[..MaxIntermediateAssistantTextChars] + "[reasoning omitted]")
                            {
                                ProtectedData = trc.ProtectedData
                            });
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    case TextContent tc:
                        if (!string.IsNullOrEmpty(tc.Text) && tc.Text.Length > MaxIntermediateAssistantTextChars)
                        {
                            rebuilt.Add(new TextContent(
                                tc.Text[..MaxIntermediateAssistantTextChars] + "[text omitted]"));
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    case FunctionCallContent fc:
                        // Truncate large argument values. The call ID and function name are
                        // always preserved; only bulk string payloads (file contents, scripts)
                        // are replaced with a size annotation.
                        if (fc.Arguments?.Any(kv => IsLargeArgValue(kv.Value)) == true)
                        {
                            var truncatedArgs = new AIFunctionArguments(
                                fc.Arguments.ToDictionary(
                                    kv => kv.Key,
                                    kv => IsLargeArgValue(kv.Value)
                                        ? TruncateArgValue(kv.Value)
                                        : kv.Value));
                            rebuilt.Add(new FunctionCallContent(
                                fc.CallId ?? fc.Name ?? string.Empty,
                                fc.Name ?? string.Empty,
                                truncatedArgs));
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    default:
                        rebuilt.Add(content);
                        break;
                }
            }

            result.Add(anyTruncated
                ? new ChatMessage(msg.Role, rebuilt) { AuthorName = msg.AuthorName }
                : msg);
        }
        return result;
    }

    private static bool IsLargeArgValue(object? value) => value switch
    {
        string s                                                                   => s.Length > MaxIntermediateArgValueChars,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => (je.GetString()?.Length ?? 0) > MaxIntermediateArgValueChars,
        _ => false
    };

    private static object? TruncateArgValue(object? value) => value switch
    {
        string s                                                                   => $"[{s.Length:N0} chars — omitted from intermediate context]",
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => $"[{je.GetString()?.Length ?? 0:N0} chars — omitted from intermediate context]",
        _ => value
    };

    /// <summary>
    /// For <c>shell_run</c> calls with identical <c>command</c> + <c>workingDirectory</c>
    /// arguments, compresses the tool result of earlier calls to a single-line outcome
    /// ("succeeded" / "failed [exit N]"). The command call itself is left intact so the
    /// sequence of attempts remains visible in context. The latest call keeps its full output.
    /// </summary>
    internal static IEnumerable<ChatMessage> CompressSupersededShellPairs(
        IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Pass 1: map each shell_run callId to its key; track the last callId per key.
        var keyById   = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            {
                if (fc.Name is not "shell_run" || fc.CallId is null) continue;
                object? cmdObj = null, dirObj = null;
                fc.Arguments?.TryGetValue("command",          out cmdObj);
                fc.Arguments?.TryGetValue("workingDirectory", out dirObj);
                var key = (cmdObj?.ToString()?.Trim() ?? string.Empty)
                        + "\0"
                        + (dirObj?.ToString() ?? string.Empty);
                keyById[fc.CallId]  = key;
                lastByKey[key]      = fc.CallId;
            }
        }

        if (keyById.Count == 0) return list;

        var toCompress = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, key) in keyById)
            if (lastByKey[key] != callId)
                toCompress.Add(callId);

        if (toCompress.Count == 0) return list;

        // Snapshot the result text for each superseded call so we can extract its outcome.
        var resultById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Tool) continue;
            foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                if (fr.CallId is not null && toCompress.Contains(fr.CallId))
                    resultById[fr.CallId] = fr.Result?.ToString() ?? string.Empty;
        }

        // Replace only the tool result for superseded calls; leave the FunctionCallContent intact.
        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role == ChatRole.Tool &&
                msg.Contents.OfType<FunctionResultContent>()
                    .Any(fr => fr.CallId is not null && toCompress.Contains(fr.CallId)))
            {
                var rebuilt = msg.Contents.Select(c =>
                {
                    if (c is FunctionResultContent fr && fr.CallId is not null && toCompress.Contains(fr.CallId))
                    {
                        resultById.TryGetValue(fr.CallId, out var text);
                        return (AIContent)new FunctionResultContent(fr.CallId, ShellOutcomeSummary(text ?? string.Empty));
                    }
                    return c;
                }).ToList<AIContent>();
                result.Add(new ChatMessage(msg.Role, rebuilt));
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    // Failures always begin with "[EXIT N]"; everything else is a success.
    private static string ShellOutcomeSummary(string resultText)
    {
        if (!resultText.StartsWith("[EXIT ", StringComparison.Ordinal)) return "succeeded";
        var end = resultText.IndexOf(']');
        return end > 0 ? $"failed {resultText[..(end + 1)]}" : "failed";
    }

    // Tools whose results are purely observational: the latest call with the same arguments
    // is the only one that matters — earlier results reflect stale state.
    private static readonly HashSet<string> ObservationalTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "grep_file", "list_files", "list_directory",
        "get_file_summary", "get_file_info", "session_context_read",
        "changes_read_latest", "git_status", "git_diff",
    };

    /// <summary>
    /// Replaces observational tool-call/result pairs that are superseded by a later call
    /// with identical arguments. Only the freshest result for each (tool, args) combination
    /// is preserved; earlier identical calls are stubbed out.
    /// </summary>
    internal static IEnumerable<ChatMessage> DropSupersededObservationalPairs(
        IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Pass 1: map each callId to its key; track the last callId seen for each key.
        var keyById     = new Dictionary<string, string>(StringComparer.Ordinal); // callId → key
        var lastByKey   = new Dictionary<string, string>(StringComparer.Ordinal); // key → last callId

        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            {
                if (fc.CallId is null || fc.Name is null) continue;
                if (!ObservationalTools.Contains(fc.Name)) continue;
                var key = BuildObservationalKey(fc);
                keyById[fc.CallId]  = key;
                lastByKey[key]      = fc.CallId;
            }
        }

        if (keyById.Count == 0) return list;

        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, key) in keyById)
            if (lastByKey[key] != callId)
                superseded.Add(callId);

        if (superseded.Count == 0) return list;

        const string FcNote   = "[superseded — repeated call with same arguments]";
        const string ToolNote = "[omitted — superseded by later identical call]";

        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                if (!msg.Contents.OfType<FunctionCallContent>()
                        .Any(fc => fc.CallId is not null && superseded.Contains(fc.CallId)))
                {
                    result.Add(msg);
                    continue;
                }
                var rebuilt = msg.Contents.Select(c =>
                {
                    if (c is FunctionCallContent fc && fc.CallId is not null && superseded.Contains(fc.CallId))
                        return (AIContent)new FunctionCallContent(fc.CallId, fc.Name ?? string.Empty,
                            new AIFunctionArguments(new Dictionary<string, object?> { ["_note"] = FcNote }));
                    return c;
                }).ToList<AIContent>();
                result.Add(new ChatMessage(msg.Role, rebuilt) { AuthorName = msg.AuthorName });
            }
            else if (msg.Role == ChatRole.Tool)
            {
                if (!msg.Contents.OfType<FunctionResultContent>()
                        .Any(fr => fr.CallId is not null && superseded.Contains(fr.CallId)))
                {
                    result.Add(msg);
                    continue;
                }
                var rebuilt = msg.Contents.Select(c =>
                {
                    if (c is FunctionResultContent fr && fr.CallId is not null && superseded.Contains(fr.CallId))
                        return (AIContent)new FunctionResultContent(fr.CallId, ToolNote);
                    return c;
                }).ToList<AIContent>();
                result.Add(new ChatMessage(msg.Role, rebuilt));
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    // Builds a deduplication key from a tool call: tool name + sorted argument entries.
    // Sorting by key makes matching argument-order-independent.
    private static string BuildObservationalKey(FunctionCallContent fc)
    {
        if (fc.Arguments is not { Count: > 0 })
            return fc.Name ?? string.Empty;

        var sb = new StringBuilder(fc.Name);
        foreach (var kv in fc.Arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(':');
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value?.ToString() ?? string.Empty);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replaces <c>write_file</c> and <c>patch_file</c> tool-call/result pairs that are
    /// superseded by a later <c>write_file</c> to the same path with compact placeholders.
    /// A call is superseded when a subsequent <c>write_file</c> overwrites the same path
    /// entirely, making the earlier write irrelevant to context.
    /// </summary>
    internal static IEnumerable<ChatMessage> DropSupersededWritePairs(
        IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Pass 1: collect write_file/patch_file calls in order; track last write_file per path.
        var writeCalls = new List<(string CallId, string Path, string ToolName)>();
        var lastWriteIdByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            {
                if (fc.Name is not ("write_file" or "patch_file") || fc.CallId is null) continue;
                object? pathObj = null;
                fc.Arguments?.TryGetValue("path", out pathObj);
                var path = pathObj?.ToString();
                if (string.IsNullOrEmpty(path)) continue;
                writeCalls.Add((fc.CallId, path!, fc.Name!));
                if (fc.Name == "write_file")
                    lastWriteIdByPath[path!] = fc.CallId;
            }
        }

        if (writeCalls.Count == 0) return list;

        // A call is superseded if a later write_file targets the same path.
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, path, _) in writeCalls)
            if (lastWriteIdByPath.TryGetValue(path, out var lastId) && callId != lastId)
                superseded.Add(callId);

        if (superseded.Count == 0) return list;

        const string FcNote      = "[superseded — later write_file for same path]";
        const string ToolNote    = "[omitted — superseded by later write_file]";

        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                if (!msg.Contents.OfType<FunctionCallContent>()
                        .Any(fc => fc.CallId is not null && superseded.Contains(fc.CallId)))
                {
                    result.Add(msg);
                    continue;
                }
                var rebuilt = msg.Contents.Select(c =>
                {
                    if (c is FunctionCallContent fc && fc.CallId is not null && superseded.Contains(fc.CallId))
                        return (AIContent)new FunctionCallContent(fc.CallId, fc.Name ?? string.Empty,
                            new AIFunctionArguments(new Dictionary<string, object?> { ["_note"] = FcNote }));
                    return c;
                }).ToList<AIContent>();
                result.Add(new ChatMessage(msg.Role, rebuilt) { AuthorName = msg.AuthorName });
            }
            else if (msg.Role == ChatRole.Tool)
            {
                if (!msg.Contents.OfType<FunctionResultContent>()
                        .Any(fr => fr.CallId is not null && superseded.Contains(fr.CallId)))
                {
                    result.Add(msg);
                    continue;
                }
                var rebuilt = msg.Contents.Select(c =>
                {
                    if (c is FunctionResultContent fr && fr.CallId is not null && superseded.Contains(fr.CallId))
                        return (AIContent)new FunctionResultContent(fr.CallId, ToolNote);
                    return c;
                }).ToList<AIContent>();
                result.Add(new ChatMessage(msg.Role, rebuilt));
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    // One ToolResultCompactionStrategy per distinct maxPairs value, shared across all agents
    // and calls that use it — the strategy is stateless (just a trigger + a count), so
    // there's no reason to reallocate it on every inner LLM call.
    private static readonly ConcurrentDictionary<int, ToolResultCompactionStrategy> _toolPairStrategies = new();

    /// <summary>
    /// Deterministic sliding-window cap: collapses tool-call/result groups beyond the most
    /// recent <paramref name="maxPairs"/> into compact summaries via MAF's
    /// <see cref="ToolResultCompactionStrategy"/>, applied unconditionally on every call
    /// (<see cref="CompactionTriggers.Always"/>) — <see cref="ToolResultCompactionStrategy.MinimumPreservedGroups"/>
    /// is the actual limiting mechanism, so this stays O(maxPairs) regardless of how many
    /// tool calls the agent has made.
    /// </summary>
    /// <remarks>
    /// Collapsing replaces the entire atomic tool-call group — the calling assistant message
    /// plus all of its tool results, including any <c>ProtectedData</c> reasoning blob — with
    /// one new assistant summary message. A <see cref="FunctionCallContent"/> is therefore
    /// never left without its matching <see cref="FunctionResultContent"/>, which strict
    /// providers require.
    /// <para>
    /// Note: <paramref name="maxPairs"/> now bounds MAF "groups" (one assistant turn plus all
    /// of its tool results, even when the turn issued several parallel calls), not individual
    /// <see cref="ChatRole.Tool"/> messages as the previous hand-rolled implementation counted.
    /// Turns with parallel tool calls collapse as a single unit rather than per call.
    /// </para>
    /// </remarks>
    internal static async Task<IEnumerable<ChatMessage>> KeepLastToolPairs(
        IEnumerable<ChatMessage> messages,
        int maxPairs,
        CancellationToken cancellationToken = default)
    {
        var strategy = _toolPairStrategies.GetOrAdd(maxPairs,
            n => new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: n));

        return await CompactionProvider.CompactAsync(strategy, messages, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Trims accumulated in-turn tool-result messages when total character count exceeds
    /// <paramref name="maxChars"/>. Oldest <see cref="ChatRole.Tool"/> result messages are
    /// replaced with a compact placeholder (preserving the <c>CallId</c> so the provider
    /// sees a structurally valid conversation). Non-tool messages are never removed.
    /// </summary>
    internal static IEnumerable<ChatMessage> TrimInTurnContext(
        IEnumerable<ChatMessage> messages,
        int maxChars)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Count chars across all messages.
        int total = 0;
        foreach (var m in list)
            foreach (var c in m.Contents)
                total += EstimateContentChars(c);

        if (total <= maxChars) return list;

        // Collect indices of ChatRole.Tool messages that can be trimmed (oldest first).
        var trimCandidates = new Queue<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].Role == ChatRole.Tool) trimCandidates.Enqueue(i);

        // Phase 1: replace oldest tool results with a tiny placeholder until under budget.
        var result = new List<ChatMessage>(list);
        const string Placeholder = "[result omitted — in-turn context trimmed]";
        while (total > maxChars && trimCandidates.Count > 0)
        {
            int idx = trimCandidates.Dequeue();
            var old = result[idx];
            int oldChars = old.Contents.Sum(c => EstimateContentChars(c));

            // Rebuild as same-role message with placeholder text per FunctionResultContent,
            // preserving CallId so the message chain stays valid for strict providers.
            var trimmedContents = old.Contents
                .OfType<FunctionResultContent>()
                .Select(fr => (AIContent)new FunctionResultContent(fr.CallId, Placeholder))
                .ToList<AIContent>();

            if (trimmedContents.Count == 0)
                trimmedContents = [new TextContent(Placeholder)];

            result[idx] = new ChatMessage(old.Role, trimmedContents);
            int newChars = result[idx].Contents.Sum(c => EstimateContentChars(c));
            total -= oldChars - newChars;
        }

        // Phase 2: if still over budget because individual retained results are larger than
        // maxChars (e.g. a single read_file of a large file), truncate their content
        // proportionally. Phase 1 cannot help when the last N messages alone exceed the budget.
        if (total > maxChars)
        {
            var remainingToolIndices = new List<int>();
            int nonToolChars = 0;
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Role == ChatRole.Tool)
                    remainingToolIndices.Add(i);
                else
                    nonToolChars += result[i].Contents.Sum(c => EstimateContentChars(c));
            }

            if (remainingToolIndices.Count > 0)
            {
                int toolBudget    = Math.Max(maxChars - nonToolChars, 0);
                int perResultMax  = Math.Max(toolBudget / remainingToolIndices.Count, 200);
                const string TruncSuffix = "\n[...truncated — in-turn budget exceeded]";

                foreach (int idx in remainingToolIndices)
                {
                    var old     = result[idx];
                    bool changed = false;
                    var rebuilt  = new List<AIContent>(old.Contents.Count);
                    foreach (var content in old.Contents)
                    {
                        if (content is FunctionResultContent fr &&
                            fr.Result is string s && s.Length > perResultMax)
                        {
                            rebuilt.Add(new FunctionResultContent(
                                fr.CallId ?? string.Empty, s[..perResultMax] + TruncSuffix));
                            changed = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                    }
                    if (changed)
                        result[idx] = new ChatMessage(old.Role, rebuilt);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Composes the full in-turn filter sequence in the order every call site applies it:
    /// drop superseded writes, drop superseded observational reads, compress superseded
    /// shell reruns, truncate intermediate reasoning, then optionally cap the tool-pair
    /// window and char budget. <paramref name="maxInTurnToolPairs"/>/
    /// <paramref name="maxInTurnChars"/> of 0 skip that step, matching the
    /// <c>if (max... &gt; 0)</c> convention each caller used before this was consolidated.
    /// </summary>
    internal static async Task<IEnumerable<ChatMessage>> ApplyInTurnFilters(
        IEnumerable<ChatMessage> messages,
        int maxInTurnToolPairs,
        int maxInTurnChars,
        CancellationToken cancellationToken = default)
    {
        messages = DropSupersededWritePairs(messages);
        messages = DropSupersededObservationalPairs(messages);
        messages = CompressSupersededShellPairs(messages);
        messages = TruncateIntermediateAssistantReasoning(messages);

        if (maxInTurnToolPairs > 0)
            messages = await KeepLastToolPairs(messages, maxInTurnToolPairs, cancellationToken);

        if (maxInTurnChars > 0)
            messages = TrimInTurnContext(messages, maxInTurnChars);

        return messages;
    }

    internal static int EstimateContentChars(AIContent content) => content switch
    {
        TextContent t           => t.Text?.Length ?? 0,
        FunctionResultContent r => r.Result is string s ? s.Length : r.Result?.ToString()?.Length ?? 0,
        FunctionCallContent c   => (c.Name?.Length ?? 0) + (c.Arguments?.Values.Sum(v =>
                                      v is System.Text.Json.JsonElement je ? je.GetRawText().Length
                                      : v?.ToString()?.Length ?? 0) ?? 0),
        // ProtectedData is the opaque blob encoding the full thinking token sequence.
        // It must be included here or budget/trim checks are completely blind to thinking cost,
        // allowing it to accumulate unchecked across tool-call rounds.
        TextReasoningContent trc => (trc.Text?.Length ?? 0) + (trc.ProtectedData?.Length ?? 0),
        _                       => 0,
    };
}
