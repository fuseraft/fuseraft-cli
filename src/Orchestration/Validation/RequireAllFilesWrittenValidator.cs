using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route unless every file listed in <c>brief.json</c>'s
/// <c>files_to_change</c> array has been written during this session.
/// </summary>
public sealed class RequireAllFilesWrittenValidator(
    string briefPath,
    string? changeLogPath = null) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // 1. Brief must exist — no brief means no authoritative file list to check.
        if (!File.Exists(briefPath))
            return RoutingValidationResult.Fail(
                $"Handoff blocked: '{briefPath}' does not exist. Write the brief (with 'files_to_change') first, then retry.");

        // 2. Parse brief.json.
        AllFilesWrittenBrief? brief;
        try
        {
            var json = await File.ReadAllTextAsync(briefPath, cancellationToken);
            brief = JsonSerializer.Deserialize<AllFilesWrittenBrief>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return RoutingValidationResult.Fail(
                $"Handoff blocked: '{briefPath}' could not be parsed: {ex.Message}. Fix JSON and retry.");
        }

        // 3. Collect required file paths from brief.
        var required = brief?.FilesToChange?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => PathHelpers.NormalizePath(p!))
            .ToList() ?? [];

        // Nothing to check — pass immediately.
        if (required.Count == 0)
            return RoutingValidationResult.Pass();

        // 4. Collect all files written this session from changes.json.
        //
        // ChangeTracker.FlushTurnAsync runs before the selection strategy (and therefore
        // before this validator), so the current turn's write_file calls are already
        // recorded by the time we get here. Using the log as the sole source removes the
        // need for fragile chat-history CallId scanning, which breaks with providers
        // (e.g. xAI) that do not reliably populate matching CallIds in MAF messages.
        //
        // Falls back to history scanning only when no log path is configured, so existing
        // deployments without ChangeTracking continue to work.
        HashSet<string> allWritten;
        if (changeLogPath is not null)
        {
            allWritten = (await CollectWrittenFilesFromChangesAsync(changeLogPath, cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var writtenThisTurn   = CollectWrittenFilesFromHistory(history);
            allWritten = writtenThisTurn.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var missing = required
            .Where(req => !allWritten.Any(w => PathHelpers.PathsMatch(w, req)))
            .Where(req => !File.Exists(req))      // pre-existing files are not the validator's concern
            .ToList();

        if (missing.Count == 0)
            return RoutingValidationResult.Pass();

        var writtenList = allWritten.Count > 0
            ? string.Join("\n", allWritten.OrderBy(f => f).Select(f => $"  ✓ {f}"))
            : "  (none)";

        return RoutingValidationResult.Fail(
            "Handoff blocked: these files from brief.json were not written this session:\n\n" +
            string.Join("\n", missing.Select(f => $"  ✗ {f}")) +
            "\n\nCreate them with write_file. Written this session:\n" +
            writtenList);
    }

    // History scanning

    private static HashSet<string> CollectWrittenFilesFromHistory(
        IList<ChatMessage> history)
    {
        // Pre-scan: build CallId → function name map from assistant messages.
        var callIdToFunctionName = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.Role == ChatRole.User) break;
            if (m.Role != ChatRole.Assistant) continue;
            foreach (var item in m.Contents)
            {
                if (item is FunctionCallContent fcc && fcc.CallId is not null && fcc.Name is not null)
                    callIdToFunctionName.TryAdd(fcc.CallId, fcc.Name);
            }
        }

        // Pass 1 — gather CallIds of successful write_file results.
        var succeededIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.User) break;
            if (msg.Role != ChatRole.Tool)  continue;

            foreach (var item in msg.Contents)
            {
                if (item is not FunctionResultContent frc) continue;
                var funcName = (frc.CallId is not null && callIdToFunctionName.TryGetValue(frc.CallId, out var n)) ? n : string.Empty;
                // AIFunctionFactory strips underscores and uses PascalCase (WriteFileAsync → WriteFile).
                // Normalize by removing underscores so "WriteFile" matches pattern "write_file".
                if (!funcName.Replace("_", "").Contains("writefile", StringComparison.OrdinalIgnoreCase)) continue;

                var result = frc.Result?.ToString() ?? string.Empty;
                if (!result.StartsWith("[ERROR]",     StringComparison.Ordinal) &&
                    !result.StartsWith("[DENIED]",    StringComparison.Ordinal) &&
                    !result.StartsWith("[NOT FOUND]", StringComparison.Ordinal))
                {
                    succeededIds.Add(frc.CallId ?? string.Empty);
                }
            }
        }

        if (succeededIds.Count == 0) return [];

        // Pass 2 — collect paths from FunctionCallContent whose CallId matches a succeeded call.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.User)      break;
            if (msg.Role != ChatRole.Assistant) continue;

            foreach (var item in msg.Contents)
            {
                if (item is not FunctionCallContent fcc) continue;
                if (!succeededIds.Contains(fcc.CallId ?? string.Empty)) continue;

                var path = ExtractPathArg(fcc.Arguments);
                if (path is not null) paths.Add(PathHelpers.NormalizePath(path));
            }
        }

        return paths;
    }

    // changes.json scanning

    private static async Task<IReadOnlySet<string>> CollectWrittenFilesFromChangesAsync(
        string logPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath)) return new HashSet<string>();

        ChangeLog log;
        try
        {
            var json = await File.ReadAllTextAsync(logPath, cancellationToken);
            log = JsonSerializer.Deserialize<ChangeLog>(json, JsonOptions) ?? new ChangeLog();
        }
        catch { return new HashSet<string>(); }

        var sessionId = log.ActiveSessionId;
        var entries = sessionId is not null
            ? log.Entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            : (IEnumerable<ChangeEntry>)log.Entries;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            foreach (var p in entry.FilesWritten)
                if (!string.IsNullOrWhiteSpace(p))
                    paths.Add(PathHelpers.NormalizePath(p));

        return paths;
    }

    // Helpers

    private static string? ExtractPathArg(IDictionary<string, object?>? args)
    {
        if (args is null) return null;
        if (args.TryGetValue("path", out var val) && val is string s) return s;
        return null;
    }

}

// Internal DTOs

internal sealed record AllFilesWrittenBrief
{
    [JsonPropertyName("files_to_change")]
    public List<string>? FilesToChange { get; init; }
}
