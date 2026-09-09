using System.Text;
using System.Text.Json;

namespace fuseraft.Infrastructure.Agents;

// Stable signature for consecutive-identical-tool-call comparison — sorted so the model varying
// key order between two otherwise-identical calls doesn't defeat detection. Tool name is
// compared separately by each caller, so this covers arguments only. Reused by both
// ReplToolLoopGuard (Cli.Commands.Repl) and AgentToolLoopGuard (this namespace) — extracted here
// so Infrastructure.Agents doesn't reach backwards into Cli.Commands.Repl for it.
internal static class ToolCallSignature
{
    internal static string Compute(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var key in args.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = args[key];
            var s = value is JsonElement je ? je.ToString() : value?.ToString() ?? "null";
            sb.Append(key).Append('=').Append(s).Append(';');
        }
        return sb.ToString();
    }
}
