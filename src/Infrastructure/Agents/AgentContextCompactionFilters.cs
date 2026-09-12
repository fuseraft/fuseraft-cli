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

    // Ratio of the reference budget at which KeepLastToolPairs actually engages inside
    // ApplyInTurnFilters — mirrors Cline's COMPACTION_TRIGGER_RATIO (sdk/packages/core/src/
    // extensions/context/compaction-shared.ts). Below this, the pair sequence is left
    // untouched so the request prefix stays byte-identical to the previous round's and a
    // provider's prompt cache can still serve it; only once a turn is actually approaching
    // its budget does the window start evicting the oldest pairs.
    internal const double CompactionTriggerRatio = 0.9;

    // Reference budget for CompactionTriggerRatio when the caller has no better number (e.g.
    // a REPL session with no configured MaxContextTokens). Matches AgentFactory's own
    // DefaultMaxInTurnChars fallback tier.
    internal const int DefaultTriggerChars = 200_000;

    // Minimum accumulated size of superseded content before DropSupersededWritePairs/
    // DropSupersededObservationalPairs actually rewrite it. Mirrors Cline's
    // DEFAULT_MIN_OUTDATED_REWRITE_BYTES (sdk/packages/core/src/session/services/
    // message-builder.ts): rewriting a stale tool result the instant it's superseded changes
    // a message in the middle of the transcript on every single re-read, which invalidates a
    // provider's prefix cache for everything after it on every call. Batching the rewrite
    // means most calls resend an identical prefix and hit the cache; only once enough
    // staleness has piled up is it worth paying the one-time cache miss to reclaim the space.
    internal const int DefaultMinSupersededDropChars = 65_536;

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

    // NOT the original value — a metadata note describing what was elided. The model has been
    // seen re-echoing this exact string as a literal argument value in a later, live tool call
    // (e.g. reconstructing a prior write_file's `content` from its own truncated history when
    // asked to move/duplicate the file), writing the placeholder itself to disk. The wording
    // must make clear this is not reusable content, not just that it was shortened.
    // FileSystemPlugin.WriteFileAsync/PatchFileAsync also guard against this directly — see
    // ElisionMarkers.PlaceholderTail — but the wording here is the first line of defense.
    private static object? TruncateArgValue(object? value) => value switch
    {
        string s                                                                   => ElisionMarkers.ArgValueNote(s.Length),
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => ElisionMarkers.ArgValueNote(je.GetString()?.Length ?? 0),
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

    // Sums the current (pre-rewrite) size of every tool result whose CallId is in
    // `supersededCallIds` — shared by DropSupersededObservationalPairs and
    // DropSupersededWritePairs to decide whether accumulated staleness clears their batch
    // threshold yet.
    private static int SupersededResultChars(IList<ChatMessage> messages, HashSet<string> supersededCallIds)
    {
        int total = 0;
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Tool) continue;
            foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                if (fr.CallId is not null && supersededCallIds.Contains(fr.CallId))
                    total += EstimateContentChars(fr);
        }
        return total;
    }

    /// <summary>
    /// Replaces observational tool-call/result pairs that are superseded by a later call
    /// with identical arguments. Only the freshest result for each (tool, args) combination
    /// is preserved; earlier identical calls are stubbed out.
    /// </summary>
    /// <param name="minBatchChars">
    /// Skip the rewrite entirely unless the superseded results found this pass total at
    /// least this many chars — see <see cref="DefaultMinSupersededDropChars"/>'s doc comment.
    /// 0 (the default) rewrites as soon as anything is superseded, matching the previous
    /// unconditional behavior; direct callers other than <see cref="ApplyInTurnFilters"/> get
    /// that behavior unless they opt into batching explicitly.
    /// </param>
    internal static IEnumerable<ChatMessage> DropSupersededObservationalPairs(
        IEnumerable<ChatMessage> messages, int minBatchChars = 0)
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

        if (minBatchChars > 0 && SupersededResultChars(list, superseded) < minBatchChars)
            return list;

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
    /// <param name="minBatchChars">
    /// Skip the rewrite entirely unless the superseded results found this pass total at
    /// least this many chars — see <see cref="DropSupersededObservationalPairs"/>'s parameter
    /// of the same name and <see cref="DefaultMinSupersededDropChars"/>'s doc comment.
    /// </param>
    internal static IEnumerable<ChatMessage> DropSupersededWritePairs(
        IEnumerable<ChatMessage> messages, int minBatchChars = 0)
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

        if (minBatchChars > 0 && SupersededResultChars(list, superseded) < minBatchChars)
            return list;

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
    //
    // MAF's Compaction namespace (ToolResultCompactionStrategy, CompactionTriggers,
    // CompactionProvider below) is still gated behind MAAI001 as of Microsoft.Agents.AI
    // 1.16.0 — this is the only place in the codebase that touches it, so the suppression
    // is scoped here rather than project-wide (see fuseraft.csproj).
#pragma warning disable MAAI001
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
#pragma warning restore MAAI001

    /// <summary>
    /// A message is trimmable if it's a <see cref="ChatRole.Tool"/> result, or a pure-text
    /// (no <see cref="FunctionCallContent"/>) <see cref="ChatRole.Assistant"/> message.
    ///
    /// The second case matters because <see cref="KeepLastToolPairs"/> (MAF's
    /// <c>ToolResultCompactionStrategy</c>) replaces evicted tool-call/result groups with a
    /// single new assistant text message — but its default formatter does not meaningfully
    /// shrink the content (an evicted group's "summary" can land within a few dozen chars of
    /// the original result's full size). Without treating that output as trimmable here, it
    /// would sit in every subsequent request untouched forever, because it's no longer a
    /// <see cref="ChatRole.Tool"/> message: <see cref="KeepLastToolPairs"/> would silently stop
    /// providing any real token-growth protection past the point its window starts evicting
    /// groups, which defeats the point of running it ahead of this trim.
    ///
    /// This is safe to treat as fair game: ordinary intermediate assistant reasoning was
    /// already truncated by <see cref="TruncateIntermediateAssistantReasoning"/> earlier in
    /// <see cref="ApplyInTurnFilters"/> (which explicitly leaves pure-text messages alone,
    /// treating them as final responses) — so a pure-text assistant message still large enough
    /// to matter by the time this runs is compaction output, not organic reasoning. It also
    /// can't be the turn's actual final answer: this trim only ever runs on the message list
    /// being sent as input to another inner LLM call inside an active tool loop, and a loop
    /// that already has a trailing pure-text assistant message wouldn't call the model again.
    /// </summary>
    private static bool IsTrimmableMessage(ChatMessage m) =>
        m.Role == ChatRole.Tool ||
        (m.Role == ChatRole.Assistant &&
         !m.Contents.OfType<FunctionCallContent>().Any() &&
         m.Contents.OfType<TextContent>().Any());

    /// <summary>
    /// Trims accumulated in-turn tool-result messages (see <see cref="IsTrimmableMessage"/>)
    /// when total character count exceeds <paramref name="maxChars"/>. Oldest results are
    /// replaced with a compact placeholder (preserving the <c>CallId</c> on tool results so the
    /// provider sees a structurally valid conversation). Everything else is never removed.
    /// </summary>
    internal static IEnumerable<ChatMessage> TrimInTurnContext(
        IEnumerable<ChatMessage> messages,
        int maxChars)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        int total = EstimateTotalChars(list);

        if (total <= maxChars) return list;

        // Collect indices of trimmable messages (oldest first).
        var trimCandidates = new Queue<int>();
        for (int i = 0; i < list.Count; i++)
            if (IsTrimmableMessage(list[i])) trimCandidates.Enqueue(i);

        // Phase 1: replace oldest tool results with a tiny placeholder until under budget.
        // Wording mirrors ElisionMarkers.ArgValueNote: not just "shortened" but explicitly not
        // the real output, so the model doesn't reuse it as data (e.g. treating an elided
        // read_file result as the actual file contents when writing it elsewhere).
        var result = new List<ChatMessage>(list);
        const string Placeholder = ElisionMarkers.ResultNote;
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
            var remainingTrimIndices = new List<int>();
            int protectedChars = 0;
            for (int i = 0; i < result.Count; i++)
            {
                if (IsTrimmableMessage(result[i]))
                    remainingTrimIndices.Add(i);
                else
                    protectedChars += result[i].Contents.Sum(c => EstimateContentChars(c));
            }

            if (remainingTrimIndices.Count > 0)
            {
                int trimBudget    = Math.Max(maxChars - protectedChars, 0);
                int perResultMax  = Math.Max(trimBudget / remainingTrimIndices.Count, 200);
                // Unlike Placeholder above, the content before this suffix IS real — only
                // everything after the cut point is missing. Says so explicitly so the model
                // doesn't treat the retained prefix as the complete result.
                const string TruncSuffix =
                    "\n[TRUNCATED HERE — everything after this point was cut for context budget; " +
                    "this is not the complete output. Re-run the tool if you need the rest.]";

                foreach (int idx in remainingTrimIndices)
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
                        else if (content is TextContent tc &&
                                 tc.Text is { Length: > 0 } text && text.Length > perResultMax)
                        {
                            rebuilt.Add(new TextContent(text[..perResultMax] + TruncSuffix));
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
    /// <param name="triggerChars">
    /// Reference budget for <see cref="CompactionTriggerRatio"/>: the sliding tool-pair
    /// window (<see cref="KeepLastToolPairs"/>) is only invoked once the current message
    /// list's estimated size reaches <see cref="CompactionTriggerRatio"/> of this number.
    /// Below that, the pair sequence — and therefore the request prefix a provider would
    /// cache — is left exactly as the previous call produced it. 0 (the default) disables
    /// the gate and collapses on every call regardless of size, matching the previous
    /// unconditional behavior, for any caller that hasn't been updated to supply a budget.
    /// The superseded-pair drops above are always batched via
    /// <see cref="DefaultMinSupersededDropChars"/> regardless of this parameter — that
    /// batching is a fixed policy, not something callers opt into per-call.
    /// </param>
    internal static async Task<IEnumerable<ChatMessage>> ApplyInTurnFilters(
        IEnumerable<ChatMessage> messages,
        int maxInTurnToolPairs,
        int maxInTurnChars,
        int triggerChars = 0,
        CancellationToken cancellationToken = default)
    {
        messages = DropSupersededWritePairs(messages, DefaultMinSupersededDropChars);
        messages = DropSupersededObservationalPairs(messages, DefaultMinSupersededDropChars);
        messages = CompressSupersededShellPairs(messages);
        messages = TruncateIntermediateAssistantReasoning(messages);

        if (maxInTurnToolPairs > 0)
        {
            var list = messages as IList<ChatMessage> ?? messages.ToList();
            var shouldCollapse = triggerChars <= 0
                || EstimateTotalChars(list) >= triggerChars * CompactionTriggerRatio;
            messages = shouldCollapse
                ? await KeepLastToolPairs(list, maxInTurnToolPairs, cancellationToken)
                : list;
        }

        if (maxInTurnChars > 0)
            messages = TrimInTurnContext(messages, maxInTurnChars);

        return messages;
    }

    /// <summary>Sums <see cref="EstimateContentChars"/> across every content item in every message.</summary>
    internal static int EstimateTotalChars(IEnumerable<ChatMessage> messages)
    {
        int total = 0;
        foreach (var m in messages)
            foreach (var c in m.Contents)
                total += EstimateContentChars(c);
        return total;
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
