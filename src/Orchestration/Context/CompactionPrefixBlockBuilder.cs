using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration.Context;

/// <summary>
/// Builds the five "prefix blocks" prepended to a compaction summary — brief snapshot, symbol
/// dependency graph, active-objective summary, reasoning excerpts, and exploration history —
/// from durable per-session state (events log, read cache, intent log, evidence store, brief
/// file). Extracted from <see cref="ConversationCompactor"/>: the one piece of its god-object
/// surface the architecture review called out by name ("split prefix-block construction into a
/// separate collaborator"), leaving mode selection, message trimming, anti-thrash tracking, and
/// usage accumulation behind as small enough not to need their own collaborators.
///
/// Stateless aside from the paths/stores shared across every block-build call and injected at
/// construction; the one call-varying value (the active session id, used only by the
/// exploration block) is passed explicitly into <see cref="BuildAsync"/> rather than held as a
/// field, since <see cref="ConversationCompactor.SetSessionId"/> can be called after this
/// collaborator already exists.
/// </summary>
internal sealed class CompactionPrefixBlockBuilder(
    CompactionConfig config,
    ILogger<ConversationCompactor> logger,
    string? changeLogPath,
    IntentLog? intentLog,
    string? eventsLogPath,
    EvidenceStore? evidenceStore,
    fuseraft.Infrastructure.Objectives.ObjectiveManager? objectiveManager,
    string? readCachePath,
    string? briefPath)
{
    /// <summary>
    /// Fetches and combines all five prefix blocks for the turn range being compacted.
    /// Brief comes first so the goal/files_to_change frame everything that follows; symbol
    /// graph, active objectives, reasoning excerpts, and exploration history follow in that
    /// order, each combined with a divider only when both sides are non-empty.
    /// </summary>
    public async Task<string> BuildAsync(
        int firstTurn, int lastTurn, string sessionId, CancellationToken cancellationToken)
    {
        var reasoningExcerpts = await ReadReasoningForRangeAsync(firstTurn, lastTurn);
        var reasoningBlock    = BuildReasoningBlock(reasoningExcerpts);
        var symbolBlock       = await BuildSymbolGraphBlockAsync(cancellationToken);
        var objectiveBlock    = await BuildObjectiveBlockAsync(cancellationToken);
        var briefBlock        = await BuildBriefBlockAsync(cancellationToken);
        var explorationBlock  = await BuildExplorationBlockAsync(sessionId, cancellationToken);
        return CombineBlocks(
            CombineBlocks(
                CombineBlocks(
                    CombineBlocks(briefBlock, symbolBlock), objectiveBlock),
                reasoningBlock),
            explorationBlock);
    }

    // Internals

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
                    if (!root.TryGetProperty("event_type", out var et) || et.GetString() != EventTypes.Reasoning) continue;
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

    private async Task<string> BuildBriefBlockAsync(CancellationToken ct)
    {
        if (briefPath is null || !File.Exists(briefPath)) return string.Empty;
        try
        {
            var json = await File.ReadAllTextAsync(briefPath, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new StringBuilder();
            sb.AppendLine("[BRIEF SNAPSHOT — goal, files_to_change, verify_command, execution_checklist]");
            sb.AppendLine();

            if (root.TryGetProperty("goal", out var goal))
                sb.AppendLine($"goal: {goal.GetString()}");

            if (root.TryGetProperty("files_to_change", out var files))
            {
                sb.AppendLine("files_to_change:");
                foreach (var f in files.EnumerateArray())
                    sb.AppendLine($"  - {f.GetString()}");
            }

            if (root.TryGetProperty("verify_command", out var verifyCmd))
                sb.AppendLine($"verify_command: {verifyCmd.GetString()}");

            if (root.TryGetProperty("execution_checklist", out var checklist))
            {
                sb.AppendLine("execution_checklist:");
                foreach (var item in checklist.EnumerateArray())
                    sb.AppendLine($"  - {item.GetString()}");
            }

            return sb.ToString().TrimEnd();
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

    private async Task<string> BuildExplorationBlockAsync(string sessionId, CancellationToken ct)
    {
        if (!config.IncludeExploration) return string.Empty;
        if (eventsLogPath is null || sessionId is not { Length: > 0 }) return string.Empty;

        var (fileReads, fileGreps) = await ParseToolCallEventsAsync(sessionId);
        var fileSizes              = ReadFileSizesFromCache();

        // When the event log has no reads for this session yet, seed from the read cache.
        // The cache is written synchronously on every read_file call and is always current.
        if (fileReads.Count == 0 && fileSizes.Count > 0)
            fileReads = fileSizes.ToDictionary(kv => kv.Key, _ => 1, StringComparer.OrdinalIgnoreCase);

        var shellPatterns = await ExtractShellGrepPatternsAsync(ct);

        if (fileReads.Count == 0 && fileGreps.Count == 0 && shellPatterns.Count == 0)
            return string.Empty;

        return BuildExplorationText(fileReads, fileGreps, shellPatterns, fileSizes);
    }

    // Scans events.jsonl for tool_call events in this session and counts read_file / grep_file calls.
    private async Task<(Dictionary<string, int> Reads, HashSet<string> Greps)> ParseToolCallEventsAsync(
        string sessionId)
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
                    if (!root.TryGetProperty("session", out var ses) || ses.GetString() != sessionId) continue;
                    if (!root.TryGetProperty("event_type", out var et) || et.GetString() != EventTypes.ToolCall) continue;
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
}
