using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration;

internal static class OrchestratorHelpers
{
    // How many recent agent messages to scan for routing keywords or signals.
    internal const int AgentMessageLookback = 3;

    // Inject a loop-warning message when the same agent has been invoked this many
    // consecutive turns without completing its task.
    internal const int ConsecutiveTurnWarningThreshold = 5;

    internal static TokenUsage? ExtractUsage(AgentResponse response)
    {
        if (response.Usage is null) return null;

        var inputTokens  = (int)(response.Usage.InputTokenCount  ?? 0L);
        var outputTokens = (int)(response.Usage.OutputTokenCount ?? 0L);

        if (inputTokens == 0 && outputTokens == 0) return null;

        return new TokenUsage(inputTokens, outputTokens);
    }

    internal static IReadOnlyList<ToolCallRecord>? ExtractToolCalls(
        IList<ChatMessage> messages,
        ILogger? logger = null,
        string agentName = AgentNames.Unknown)
    {
        var calls   = new List<(string CallId, string Name, string? ArgsSummary, int ArgsCharCount)>();
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);

        try
        {
            foreach (var msg in messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        var argsJson     = fc.Arguments is null ? "" : JsonSerializer.Serialize(fc.Arguments);
                        var argsCharCount = argsJson.Length;
                        calls.Add((fc.CallId ?? fc.Name, fc.Name, ToolCallHelper.SummarizeArgs(fc.Arguments), argsCharCount));
                    }
                    else if (content is FunctionResultContent fr)
                    {
                        var key  = fr.CallId ?? string.Empty;
                        var text = fr.Result?.ToString() ?? string.Empty;
                        var ok   = !text.StartsWith("[ERROR]",     StringComparison.Ordinal)
                                && !text.StartsWith("[DENIED]",    StringComparison.Ordinal)
                                && !text.StartsWith("[TIMEOUT]",   StringComparison.Ordinal)
                                && !text.StartsWith("[NOT FOUND]", StringComparison.Ordinal)
                                && !text.StartsWith("[EXIT ",      StringComparison.Ordinal);
                        if (!string.IsNullOrEmpty(key)) results[key] = ok;

                        if (!ok && logger is not null)
                        {
                            var toolName = calls.LastOrDefault(c => c.CallId == key).Name ?? key;
                            // Show only the first line of the result so the WRN message fits on one
                            // terminal line and doesn't bleed into the live-status spinner display.
                            var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
                            var preview   = firstLine.Length > 60 ? firstLine[..57] + "…" : firstLine;
                            logger.LogWarning(
                                "[{Agent}] Tool '{Tool}' failed: {ResultPreview}",
                                agentName, toolName, preview);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[{Agent}] Failed to parse tool calls from agent response — tool call records will be incomplete.",
                agentName);
        }

        if (calls.Count == 0) return null;

        return calls
            .Select(c => new ToolCallRecord(
                c.Name,
                c.ArgsSummary,
                results.TryGetValue(c.CallId, out var s) ? s : true,
                c.ArgsCharCount))
            .ToList();
    }

    internal static string? GetArg(IReadOnlyDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }

    // Counts how many consecutive assistant turns from agentName appear at the tail of
    // history, stopping at any user message or a different agent's turn.
    internal static int CountConsecutiveAgentTurns(IList<ChatMessage> history, string agentName)
    {
        int consecutive = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (string.IsNullOrEmpty(msg.Text)) continue;
            if (msg.Role == ChatRole.User) break;
            if (!string.Equals(msg.AuthorName, agentName, StringComparison.OrdinalIgnoreCase)) break;
            consecutive++;
        }
        return consecutive;
    }

    // Removes ``` code-fenced blocks from a string, keeping surrounding prose.
    internal static string StripCodeFences(string text)
    {
        var sb   = new System.Text.StringBuilder();
        bool in_ = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                in_ = !in_;
                continue;
            }
            if (!in_) sb.AppendLine(line);
        }
        return sb.ToString();
    }
}
