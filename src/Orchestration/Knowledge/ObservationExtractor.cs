using System.Text;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Knowledge;

/// <summary>
/// Extracts factual <see cref="Observation"/> records from agent message history.
///
/// <para>
/// Unlike the conversation text (what agents <em>said</em>), observations capture
/// what agents <em>learned</em> from tool calls — file content, grep matches, shell
/// output — in a form that survives compaction even when the raw tool results are
/// truncated or dropped from the message window.
/// </para>
///
/// <para>
/// Observations are produced at compaction time and injected into the summary so
/// future agents resume with ground-truth findings rather than inferred context.
/// </para>
/// </summary>
public static class ObservationExtractor
{
    private const int MaxEvidenceChars = 500;
    private const int MaxFindingChars  = 200;

    // Tools that represent genuine discoveries (reads/searches).
    private static readonly HashSet<string> DiscoveryTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "grep_file", "get_file_summary",
        "search_content", "search_files",
    };

    // Tools that represent state changes (writes/shells).
    private static readonly HashSet<string> ActionTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "patch_file", "delete_file",
        "shell_run", "shell_run_script",
    };

    /// <summary>
    /// Extracts observations from a sequence of <see cref="ChatMessage"/> records.
    /// Only <see cref="ChatRole.Tool"/> result messages that correspond to discovery tools
    /// are processed; action tools produce applied-change records.
    /// </summary>
    public static IReadOnlyList<Observation> Extract(
        IReadOnlyList<ChatMessage> messages,
        string?                   agentName  = null,
        int                       turnIndex  = 0)
    {
        if (messages.Count == 0) return [];

        // Build callId → (toolName, agentAuthor, args) index from assistant messages.
        var callMap = new Dictionary<string, (string Tool, string? Agent, IDictionary<string, object?>? Args)>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            foreach (var c in msg.Contents)
            {
                if (c is FunctionCallContent fc && fc.CallId is not null)
                    callMap[fc.CallId] = (fc.Name ?? string.Empty, msg.AuthorName ?? agentName, fc.Arguments);
            }
        }

        var observations = new List<Observation>();
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Tool) continue;

            foreach (var c in msg.Contents)
            {
                if (c is not FunctionResultContent fr) continue;

                var callId  = fr.CallId ?? string.Empty;
                var rawText = fr.Result is string s ? s : fr.Result?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(rawText)) continue;

                // Skip placeholder strings injected by context trimmers.
                if (rawText.StartsWith("[result omitted", StringComparison.OrdinalIgnoreCase) ||
                    rawText.StartsWith("[ERROR]",         StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!callMap.TryGetValue(callId, out var meta)) continue;

                var (toolName, author, args) = meta;
                float confidence;
                string finding;

                if (DiscoveryTools.Contains(toolName))
                {
                    confidence = 0.85f;
                    finding    = BuildDiscoveryFinding(toolName, rawText, callId, callMap);
                }
                else if (ActionTools.Contains(toolName))
                {
                    confidence = 0.90f;
                    finding    = BuildActionFinding(toolName, rawText);
                }
                else
                {
                    continue; // Skip other tool types.
                }

                observations.Add(new Observation
                {
                    Source     = toolName,
                    Evidence   = Truncate(rawText, MaxEvidenceChars),
                    Finding    = finding,
                    Entity     = ExtractEntityFromArgs(toolName, args),
                    AgentName  = author,
                    TurnIndex  = turnIndex,
                    Confidence = confidence,
                });
            }
        }

        return observations;
    }

    // Derives the primary entity from tool call arguments.
    private static string? ExtractEntityFromArgs(string tool, IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return null;
        // Prefer explicit path/file arguments.
        foreach (var key in new[] { "path", "file_path", "file", "filename" })
            if (args.TryGetValue(key, out var v) && v is string s && s.Length > 0) return s;
        // For search/grep tools, use the pattern or query as the entity.
        if (args.TryGetValue("pattern", out var pat) && pat is string p && p.Length > 0) return p;
        if (args.TryGetValue("query",   out var q)   && q   is string qs && qs.Length > 0) return qs;
        // Fall back to the first non-empty string argument.
        return args.Values.OfType<string>().FirstOrDefault(s => s.Length > 0);
    }

    // Builds a concise finding from a discovery tool result.
    private static string BuildDiscoveryFinding(
        string tool,
        string rawText,
        string callId,
        Dictionary<string, (string Tool, string? Agent, IDictionary<string, object?>? Args)> callMap)
    {
        var text = Truncate(rawText, MaxFindingChars);

        return tool.ToLowerInvariant() switch
        {
            "read_file"        => $"File content: {text}",
            "grep_file"        => $"Grep match: {text}",
            "get_file_summary" => $"File summary: {text}",
            "search_content"   => $"Search result: {text}",
            "search_files"     => $"Files found: {text}",
            _                  => text,
        };
    }

    // Builds a concise finding from an action tool result.
    private static string BuildActionFinding(string tool, string rawText)
    {
        var success = !rawText.StartsWith("[ERROR]",   StringComparison.OrdinalIgnoreCase) &&
                      !rawText.StartsWith("[DENIED]",  StringComparison.OrdinalIgnoreCase) &&
                      !rawText.StartsWith("[TIMEOUT]", StringComparison.OrdinalIgnoreCase);

        return tool.ToLowerInvariant() switch
        {
            "write_file" or "patch_file" =>
                success ? "File written successfully." : $"Write failed: {Truncate(rawText, 80)}",
            "delete_file" =>
                success ? "File deleted." : $"Delete failed: {Truncate(rawText, 80)}",
            "shell_run" or "shell_run_script" =>
                success ? $"Command output: {Truncate(rawText, MaxFindingChars)}"
                        : $"Command failed: {Truncate(rawText, 80)}",
            _ => Truncate(rawText, MaxFindingChars),
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── AgentMessage-level extraction ────────────────────────────────────────

    /// <summary>
    /// Builds a compact tool-trace block from <see cref="AgentMessage.ToolCalls"/> records,
    /// suitable for injecting into a compaction prompt so the LLM summariser knows what
    /// operations were actually attempted even when the raw tool results are unavailable.
    /// </summary>
    public static string? BuildToolTraceBlock(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0) return null;

        var sb     = new StringBuilder();
        bool any   = false;

        foreach (var msg in messages)
        {
            if (msg.ToolCalls is not { Count: > 0 } calls) continue;
            foreach (var call in calls)
            {
                var icon    = call.Succeeded ? "✓" : "✗";
                var argPart = string.IsNullOrWhiteSpace(call.ArgsSummary)
                    ? string.Empty
                    : $"({call.ArgsSummary})";
                sb.AppendLine($"  Turn {msg.TurnIndex + 1} [{msg.AgentName}]: {icon} {call.Name}{argPart}");
                any = true;
            }
        }

        if (!any) return null;

        return "[TOOL CALL TRACE — what agents actually did]\n" + sb.ToString().TrimEnd();
    }
}
