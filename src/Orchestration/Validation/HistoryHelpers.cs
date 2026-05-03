using Microsoft.Extensions.AI;

namespace fuseraft.Orchestration.Validation;

internal static class HistoryHelpers
{
    internal static bool MatchesPattern(string command, string pattern) =>
        pattern.Split('|').Any(alt =>
            command.Contains(alt.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static string? FindFunctionName(
        IList<ChatMessage> history,
        string? callId,
        int fromIndex)
    {
        if (callId is null) return null;

        for (int i = fromIndex - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.User) break;
            if (msg.Role != ChatRole.Assistant) continue;

            foreach (var item in msg.Contents)
            {
                if (item is FunctionCallContent fcc &&
                    string.Equals(fcc.CallId, callId, StringComparison.Ordinal))
                    return fcc.Name;
            }
        }

        return null;
    }

    internal static string? FindCommand(
        IList<ChatMessage> history,
        string? callId,
        int fromIndex)
    {
        if (callId is null) return null;

        for (int i = fromIndex - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.User) break;
            if (msg.Role != ChatRole.Assistant) continue;

            foreach (var item in msg.Contents)
            {
                if (item is not FunctionCallContent fcc) continue;
                if (!string.Equals(fcc.CallId, callId, StringComparison.Ordinal)) continue;

                if (fcc.Arguments is null) return null;

                if (fcc.Arguments.TryGetValue("command", out var cmdObj) && cmdObj is string cmdStr)
                    return cmdStr;

                foreach (var kv in fcc.Arguments)
                {
                    if (kv.Value is string s) return s;
                }

                return null;
            }
        }

        return null;
    }
}
