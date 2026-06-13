using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration.Tracking;


/// <summary>
/// Automatically records every tool call made by any agent into a structured JSON log
/// on disk (<c>.fuseraft/state/changes.json</c> by default).
///
/// <para>
/// Call <see cref="WrapAgent"/> on each agent after construction to attach the capturing
/// middleware. Each invocation is captured after the plugin function returns — including the
/// arguments and whether it succeeded — so the log reflects what actually happened, not
/// what an agent claimed in prose.
/// </para>
///
/// <para>
/// Call <see cref="FlushTurnAsync"/> after each agent text response to drain the in-memory
/// queue and append a <see cref="ChangeEntry"/> to the log file.  Turns where no tracked
/// calls occurred produce no entry.
/// </para>
///
/// <para>Tracked functions: <c>write_file</c>, <c>patch_file</c>, <c>copy_file</c>,
/// <c>move_file</c>, <c>delete_file</c>, <c>delete_directory</c>, <c>shell_run</c>,
/// <c>shell_run_script</c>, <c>git_commit</c>.</para>
/// </summary>
public sealed class ChangeTracker
{
    private readonly string _logPath;
    private readonly EventEmitter? _eventEmitter;
    private readonly EvidenceStore? _evidenceStore;
    private readonly IntentLog? _intentLog;
    private readonly RepositoryGraphBuilder? _graphBuilder;
    private readonly ILogger<ChangeTracker>? _logger;
    private readonly StateProjector? _stateProjector;
    private readonly ConcurrentQueue<InvocationRecord> _pending = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private string? _sessionId;

    // Current turn index — set by BeginTurn before each agent.RunAsync call so that
    // CapturingMiddleware can stamp intents with the correct turn index even before
    // FlushTurnAsync is called. volatile: written from the orchestration thread and
    // read from middleware callbacks that may run on a different thread-pool thread.
    private volatile int _currentTurnIndex = -1;
    private volatile string _currentAgentName = string.Empty;

    private static readonly string[] TrackedSubstrings =
        ["write_file", "patch_file", "delete_file", "delete_directory",
         "copy_file", "move_file",
         "shell_run", "shell_run_script", "shell_run_background",
         "git_commit"];

    // Separate tracking for read-only symbol searches — results go to the evidence
    // graph as SymbolDefinition nodes but do NOT produce ChangeEntry records.
    private static readonly string[] SymbolTrackedSubstrings = ["search_symbol"];
    private readonly ConcurrentQueue<SymbolSearchRecord> _symbolPending = new();

    // Separate tracking for call-site searches — results go to the evidence graph as
    // SymbolReference nodes. Kept distinct from SymbolTrackedSubstrings so the two flows
    // emit different node types without a discriminator field.
    private static readonly string[] CallerTrackedSubstrings = ["search_callers"];
    private readonly ConcurrentQueue<CallerSearchRecord> _callerPending = new();

    // AIFunctionFactory strips underscores and uses PascalCase (e.g. WriteFileAsync → WriteFile,
    // RunAsync → Run). Normalize both sides by removing underscores before comparing.
    private static bool FunctionNameMatches(string name, string pattern) =>
        name.Replace("_", "").Contains(
            pattern.Replace("_", ""),
            StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ChangeTracker(string logPath, EventEmitter? eventEmitter = null, EvidenceStore? evidenceStore = null, IntentLog? intentLog = null, ILogger<ChangeTracker>? logger = null, RepositoryGraphBuilder? graphBuilder = null, StateProjector? stateProjector = null)
    {
        _logPath        = logPath;
        _eventEmitter   = eventEmitter;
        _evidenceStore  = evidenceStore;
        _intentLog      = intentLog;
        _graphBuilder   = graphBuilder;
        _logger         = logger;
        _stateProjector = stateProjector;
    }

    /// <summary>
    /// Marks <paramref name="sessionId"/> as the active session and persists it to
    /// <c>ActiveSessionId</c> in the log file so <c>TestReportValid</c> check 8 can
    /// filter to only commands recorded in this session. Call once after the checkpoint
    /// is established, before the orchestration loop begins.
    /// </summary>
    public async Task SetSessionIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessionId = sessionId;

        // Stamp the flat change log, evidence graph, and intent log.
        if (_evidenceStore is not null)
            await _evidenceStore.SetSessionIdAsync(sessionId, cancellationToken);
        _intentLog?.SetSessionId(sessionId);
        _stateProjector?.SetSessionId(sessionId);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_logPath));
            if (dir is not null) Directory.CreateDirectory(dir);

            ChangeLog log;
            if (File.Exists(_logPath))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(_logPath, cancellationToken);
                    log = JsonSerializer.Deserialize<ChangeLog>(raw, JsonOpts) ?? new ChangeLog();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "ChangeTracker: failed to load '{Path}' — change log reset.", _logPath);
                    log = new ChangeLog();
                }
            }
            else
            {
                log = new ChangeLog();
            }

            log = log with { ActiveSessionId = sessionId };
            await File.WriteAllTextAsync(_logPath, JsonSerializer.Serialize(log, JsonOpts), cancellationToken);
        }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// Notifies the tracker that a new agent turn is starting. Call immediately before
    /// <c>agent.RunAsync</c> so that intent log entries written during the turn carry
    /// the correct turn index and agent name.
    /// </summary>
    public void BeginTurn(string agentName, int turnIndex)
    {
        _currentAgentName  = agentName;
        _currentTurnIndex  = turnIndex;
    }

    /// <summary>
    /// Wraps <paramref name="agent"/> with the capturing function middleware so every
    /// tool call is recorded. Returns the middleware-wrapped agent.
    /// </summary>
    public AIAgent WrapAgent(AIAgent agent, string agentName) =>
        agent.AsBuilder()
             .Use((a, ctx, next, ct) => CapturingMiddleware(a, ctx, next, ct, agentName))
             .Build();

    // Flush

    /// <summary>
    /// Drains the pending invocation queue, builds a <see cref="ChangeEntry"/> for the
    /// completed agent turn, and appends it to the log file on disk.
    /// No-ops when no tracked calls were recorded this turn.
    /// </summary>
    public async Task FlushTurnAsync(
        string agentName,
        int turnIndex,
        CancellationToken cancellationToken = default)
    {
        var records = new List<InvocationRecord>();
        while (_pending.TryDequeue(out var r)) records.Add(r);

        // Symbol and caller nodes are read-only and flush independently of change records
        // so recon turns (which only call search_symbol / search_callers) still populate
        // the evidence graph.
        if (_evidenceStore is not null)
        {
            var symRecords = new List<SymbolSearchRecord>();
            while (_symbolPending.TryDequeue(out var sr)) symRecords.Add(sr);
            if (symRecords.Count > 0)
                await EmitSymbolEvidenceNodesAsync(agentName, turnIndex, symRecords, cancellationToken);

            var callerRecords = new List<CallerSearchRecord>();
            while (_callerPending.TryDequeue(out var cr)) callerRecords.Add(cr);
            if (callerRecords.Count > 0)
                await EmitCallerEvidenceNodesAsync(agentName, turnIndex, callerRecords, cancellationToken);
        }

        try
        {
            if (records.Count == 0) return;

            var entry = new ChangeEntry
            {
                Agent     = agentName,
                TurnIndex = turnIndex,
                Timestamp = DateTime.UtcNow,
                SessionId = _sessionId,

                FilesWritten = [.. records
                    .Where(r => (FunctionNameMatches(r.Name, "write_file") || FunctionNameMatches(r.Name, "patch_file")) && r.Succeeded)
                    .Select(r => OrchestratorHelpers.GetArg(r.Args, "path"))
                    .Concat(records
                        .Where(r => FunctionNameMatches(r.Name, "copy_file") && r.Succeeded)
                        .Select(r => OrchestratorHelpers.GetArg(r.Args, "destination")))
                    .Concat(records
                        .Where(r => FunctionNameMatches(r.Name, "move_file") && r.Succeeded)
                        .Select(r => OrchestratorHelpers.GetArg(r.Args, "destination")))
                    .OfType<string>()],

                FilesDeleted = [.. records
                    .Where(r => FunctionNameMatches(r.Name, "delete_file") && r.Succeeded)
                    .Select(r => OrchestratorHelpers.GetArg(r.Args, "path"))
                    .Concat(records
                        .Where(r => FunctionNameMatches(r.Name, "delete_directory") && r.Succeeded)
                        .Select(r => OrchestratorHelpers.GetArg(r.Args, "path")))
                    .Concat(records
                        .Where(r => FunctionNameMatches(r.Name, "move_file") && r.Succeeded)
                        .Select(r => OrchestratorHelpers.GetArg(r.Args, "source")))
                    .OfType<string>()],

                CommandsRun = [.. records
                    .Where(r => FunctionNameMatches(r.Name, "shell_run"))
                    .Select(r => new CommandRecord
                    {
                        Command   = OrchestratorHelpers.GetArg(r.Args, "command") ?? OrchestratorHelpers.GetArg(r.Args, "script") ?? "(script)",
                        Succeeded = r.Succeeded,
                        Output    = r.Output
                    })],

                GitCommits = [.. records
                    .Where(r => FunctionNameMatches(r.Name, "git_commit") && r.Succeeded)
                    .Select(r => OrchestratorHelpers.GetArg(r.Args, "message"))
                    .OfType<string>()]
            };

            if (!entry.FilesWritten.Any() && !entry.FilesDeleted.Any() &&
                !entry.CommandsRun.Any()  && !entry.GitCommits.Any())
                return;

            // Emit typed evidence nodes for the evidence graph (alongside flat changes.json).
            if (_evidenceStore is not null)
                await EmitEvidenceNodesAsync(agentName, turnIndex, records, cancellationToken);

            // Emit artifact_deleted for every file removed this turn.
            if (_eventEmitter is not null)
            {
                foreach (var deleted in entry.FilesDeleted)
                    _ = _eventEmitter.EmitAsync(EventTypes.ArtifactDeleted, agent: agentName, turn: turnIndex,
                        payload: new { path = deleted });
            }

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_logPath));
                if (dir is not null) Directory.CreateDirectory(dir);

                ChangeLog log;
                if (File.Exists(_logPath))
                {
                    try
                    {
                        var raw = await File.ReadAllTextAsync(_logPath, cancellationToken);
                        log = JsonSerializer.Deserialize<ChangeLog>(raw, JsonOpts) ?? new ChangeLog();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "ChangeTracker: failed to load '{Path}' during flush — change log reset.", _logPath);
                        log = new ChangeLog();
                    }
                }
                else
                {
                    log = new ChangeLog();
                }

                log.Entries.Add(entry);
                await File.WriteAllTextAsync(_logPath, JsonSerializer.Serialize(log, JsonOpts), cancellationToken);
            }
            finally { _fileLock.Release(); }
        }
        finally
        {
            if (_stateProjector is not null)
            {
                try   { await _stateProjector.ProjectAsync(records, agentName, turnIndex, cancellationToken); }
                catch (Exception ex) { _logger?.LogWarning(ex, "StateProjector.ProjectAsync failed (turn {Turn}).", turnIndex); }
            }
        }
    }

    // Builds typed EvidenceNode objects from the raw invocation records and persists
    // them to the EvidenceStore. Called from FlushTurnAsync when a store is configured.
    private async Task EmitEvidenceNodesAsync(
        string agentName,
        int turnIndex,
        List<InvocationRecord> records,
        CancellationToken ct)
    {
        var nodes = new List<EvidenceNode>();
        var edges = new List<EvidenceEdge>();
        var now   = DateTime.UtcNow;

        // File writes — one node per successful write_file / patch_file / copy_file / move_file (destination).
        foreach (var r in records.Where(r =>
            (FunctionNameMatches(r.Name, "write_file") || FunctionNameMatches(r.Name, "patch_file") ||
             FunctionNameMatches(r.Name, "copy_file")  || FunctionNameMatches(r.Name, "move_file"))
            && r.Succeeded))
        {
            var path = OrchestratorHelpers.GetArg(r.Args, "destination") // copy_file / move_file use "destination"
                    ?? OrchestratorHelpers.GetArg(r.Args, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            // Compute content hash from the file on disk if it exists.
            string? contentHash = null;
            if (File.Exists(path))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(path, ct);
                    contentHash = EvidenceStore.HashContent(content);
                }
                catch { /* hash is best-effort */ }
            }

            nodes.Add(new EvidenceNode
            {
                NodeType    = "FileWrite",
                Timestamp   = now,
                Agent       = agentName,
                Turn        = turnIndex,
                SessionId   = _sessionId,
                Path        = path,
                ContentHash = contentHash,
            });
        }

        // File deletes (delete_file, delete_directory, move_file source).
        foreach (var r in records.Where(r =>
            (FunctionNameMatches(r.Name, "delete_file") || FunctionNameMatches(r.Name, "delete_directory") ||
             FunctionNameMatches(r.Name, "move_file"))
            && r.Succeeded))
        {
            var path = FunctionNameMatches(r.Name, "move_file")
                ? OrchestratorHelpers.GetArg(r.Args, "source")
                : OrchestratorHelpers.GetArg(r.Args, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            nodes.Add(new EvidenceNode
            {
                NodeType  = "FileDelete",
                Timestamp = now,
                Agent     = agentName,
                Turn      = turnIndex,
                SessionId = _sessionId,
                Path      = path,
            });
        }

        // Shell commands — one node per shell_run call (succeeded or not).
        foreach (var r in records.Where(r => FunctionNameMatches(r.Name, "shell_run")))
        {
            var command = OrchestratorHelpers.GetArg(r.Args, "command") ?? OrchestratorHelpers.GetArg(r.Args, "script") ?? "(script)";
            var output  = r.Output;
            var exitCode = r.Succeeded ? 0 : 1;

            // Try to parse the actual exit code from the result text (e.g. "[EXIT 2]").
            if (r.Output is not null && r.Output.StartsWith("[EXIT ", StringComparison.Ordinal))
            {
                var bracket = r.Output.IndexOf(']');
                if (bracket > 6 && int.TryParse(r.Output[6..bracket], out var parsed))
                    exitCode = parsed;
            }

            nodes.Add(new EvidenceNode
            {
                NodeType   = "CommandRun",
                Timestamp  = now,
                Agent      = agentName,
                Turn       = turnIndex,
                SessionId  = _sessionId,
                Command    = command,
                ExitCode   = exitCode,
                Output     = output,
                OutputHash = EvidenceStore.HashContent(output),
            });
        }

        // Git commits.
        foreach (var r in records.Where(r => FunctionNameMatches(r.Name, "git_commit") && r.Succeeded))
        {
            var message = OrchestratorHelpers.GetArg(r.Args, "message");
            if (string.IsNullOrWhiteSpace(message)) continue;

            nodes.Add(new EvidenceNode
            {
                NodeType      = "GitCommit",
                Timestamp     = now,
                Agent         = agentName,
                Turn          = turnIndex,
                SessionId     = _sessionId,
                CommitMessage = message,
            });
        }

        // Edges: link FileWrite nodes to the CommandRun in the same turn that may have
        // produced them (produced_by). This is a heuristic — within one turn, a successful
        // build command "produced" the files written in that same turn.
        var commandNodes = nodes.Where(n => n.NodeType == "CommandRun" && n.ExitCode == 0).ToList();
        var fileNodes    = nodes.Where(n => n.NodeType == "FileWrite").ToList();

        if (commandNodes.Count > 0 && fileNodes.Count > 0)
        {
            foreach (var fileNode in fileNodes)
            foreach (var cmdNode in commandNodes)
                edges.Add(new EvidenceEdge
                {
                    From     = fileNode.Id,
                    To       = cmdNode.Id,
                    Relation = "produced_by",
                });
        }

        await _evidenceStore!.RecordAsync(nodes, edges, ct);

        // Incrementally rebuild repository graph nodes for every written .cs file so that
        // graph_search and adr_governs traversal reflect the latest source structure.
        if (_graphBuilder is not null)
        {
            var writtenPaths = records
                .Where(r =>
                    (FunctionNameMatches(r.Name, "write_file") || FunctionNameMatches(r.Name, "patch_file") ||
                     FunctionNameMatches(r.Name, "copy_file")  || FunctionNameMatches(r.Name, "move_file"))
                    && r.Succeeded)
                .Select(r => OrchestratorHelpers.GetArg(r.Args, "destination") ?? OrchestratorHelpers.GetArg(r.Args, "path"))
                .OfType<string>()
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

            foreach (var path in writtenPaths)
            {
                var abs = Path.GetFullPath(path);
                _ = _graphBuilder.RebuildFileAsync(abs, CancellationToken.None); // fire-and-forget
                if (_eventEmitter is not null)
                    _ = _eventEmitter.EmitAsync(EventTypes.ArtifactUpdated, agent: agentName, turn: turnIndex,
                        payload: new { path = abs, kind = "repository_graph" });
            }
        }
    }

    // Parses search_symbol output to extract SymbolDefinition nodes for the evidence graph.
    // Runs independently of FlushTurn's change-entry guard so recon-only turns still record.
    private async Task EmitSymbolEvidenceNodesAsync(
        string agentName,
        int turnIndex,
        List<SymbolSearchRecord> records,
        CancellationToken ct)
    {
        var nodes = new List<EvidenceNode>();
        var now   = DateTime.UtcNow;

        foreach (var record in records)
        {
            foreach (var (filePath, kind) in ParseSymbolSearchOutput(record.Output))
            {
                nodes.Add(new EvidenceNode
                {
                    NodeType   = "SymbolDefinition",
                    Timestamp  = now,
                    Agent      = agentName,
                    Turn       = turnIndex,
                    SessionId  = _sessionId,
                    Path       = filePath,
                    SymbolName = record.Symbol,
                    SymbolKind = kind,
                });
            }
        }

        if (nodes.Count > 0)
            await _evidenceStore!.RecordAsync(nodes, null, ct);
    }

    // Parses search_callers output to extract SymbolReference nodes for the evidence graph.
    // TargetFile is resolved from any existing SymbolDefinition nodes in the store so the
    // reference graph can be traversed in both directions (definition → callers, caller → definition).
    private async Task EmitCallerEvidenceNodesAsync(
        string agentName,
        int turnIndex,
        List<CallerSearchRecord> records,
        CancellationToken ct)
    {
        var nodes = new List<EvidenceNode>();
        var now   = DateTime.UtcNow;

        foreach (var record in records)
        {
            // Resolve definition file(s) for the symbol — best-effort; null when symbol
            // is external or recon hasn't run yet.
            var definitionFiles = await _evidenceStore!.FindDefinitionFilesAsync(record.Symbol, ct);
            var targetFile      = definitionFiles.Count > 0 ? definitionFiles[0] : null;

            // ParseSymbolSearchOutput is reused: search_callers emits the same :L format.
            // The "kind" yield value is the call-site content snippet (not a definition kind)
            // so it's discarded here.
            foreach (var (callerFile, _) in ParseSymbolSearchOutput(record.Output))
            {
                nodes.Add(new EvidenceNode
                {
                    NodeType   = "SymbolReference",
                    Timestamp  = now,
                    Agent      = agentName,
                    Turn       = turnIndex,
                    SessionId  = _sessionId,
                    Path       = callerFile,
                    SymbolName = record.Symbol,
                    TargetFile = targetFile,
                });
            }
        }

        if (nodes.Count > 0)
            await _evidenceStore!.RecordAsync(nodes, null, ct);
    }

    // Parses search_symbol output lines into (filePath, kind) pairs.
    // Output format per line: "path/to/file.ext:L42  <content>"
    private static IEnumerable<(string FilePath, string Kind)> ParseSymbolSearchOutput(string output)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('[')) continue;

            // Locate :L separator — present on every result line
            var colonL = trimmed.IndexOf(":L", StringComparison.Ordinal);
            if (colonL <= 0) continue;

            var filePath = trimmed[..colonL].Trim();
            if (string.IsNullOrEmpty(filePath) || !seen.Add(filePath)) continue;

            // Extract content after the line-number and whitespace
            var rest     = trimmed[(colonL + 1)..];
            var spaceIdx = rest.IndexOf("  ", StringComparison.Ordinal);
            var content  = spaceIdx >= 0 ? rest[(spaceIdx + 2)..] : rest;

            yield return (filePath, InferSymbolKind(content));
        }
    }

    // Returns a coarse symbol kind inferred from the definition line content.
    private static string InferSymbolKind(string content)
    {
        var c = content.ToLowerInvariant();
        if (c.Contains(" class ")    || c.StartsWith("class "))    return "class";
        if (c.Contains(" interface ") || c.StartsWith("interface ")) return "interface";
        if (c.Contains(" record ")   || c.StartsWith("record "))   return "record";
        if (c.Contains(" struct ")   || c.StartsWith("struct "))   return "struct";
        if (c.Contains(" enum ")     || c.StartsWith("enum "))     return "enum";
        if (c.Contains(" def ")      || c.StartsWith("def ")   ||
            c.Contains(" func ")     || c.StartsWith("func ")  ||
            c.Contains(" fn ")       || c.StartsWith("fn ")    ||
            c.Contains("function "))                               return "function";
        return "symbol";
    }

    // MAF function middleware — intercepts every tool call and records tracked ones.
    private async ValueTask<object?> CapturingMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken,
        string agentName)
    {
        var name = context.Function.Name;
        var isTracked = TrackedSubstrings.Any(s => FunctionNameMatches(name, s));

        // Record intent BEFORE execution so that interrupted sessions can replay
        // or skip in-flight operations on resume.
        string? intentId = null;
        if (isTracked && _intentLog is not null)
        {
            try
            {
                intentId = await _intentLog.RecordPendingAsync(
                    agent:        agentName,
                    turnIndex:    _currentTurnIndex,
                    functionName: name,
                    args:         context.Arguments,
                    ct:           cancellationToken);
            }
            catch { /* intent log is best-effort; never block the tool call */ }
        }

        var result = await next(context, cancellationToken);

        var resultText = result?.ToString() ?? string.Empty;
        var succeeded  = !resultText.StartsWith("[ERROR]",     StringComparison.Ordinal)
                      && !resultText.StartsWith("[DENIED]",    StringComparison.Ordinal)
                      && !resultText.StartsWith("[TIMEOUT]",   StringComparison.Ordinal)
                      && !resultText.StartsWith("[NOT FOUND]", StringComparison.Ordinal)
                      && !resultText.StartsWith("[EXIT ",      StringComparison.Ordinal);

        // Update intent status now that we know the outcome.
        if (intentId is not null && _intentLog is not null)
        {
            try
            {
                var status = succeeded ? IntentStatus.Applied : IntentStatus.Failed;
                var error  = succeeded ? null
                    : resultText.Length > 300 ? resultText[..300] + "…" : resultText;
                await _intentLog.UpdateStatusAsync(intentId, status, error, CancellationToken.None);
            }
            catch { /* best-effort */ }
        }

        // Emit a real-time tool_call event for every tool call so observers can see
        // what the agent is doing within a turn — reads, searches, writes, shells, all of it.
        // For shell_run, include a truncated copy of the output so the event log is
        // self-contained: no need to cross-reference changes.json to see a build error.
        // Fire-and-forget — never block the tool call itself.
        if (_eventEmitter is not null)
        {
            var arg = OrchestratorHelpers.GetArg(context.Arguments, "path")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "source")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "destination")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "command")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "script")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "message")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "directory")
                   ?? OrchestratorHelpers.GetArg(context.Arguments, "query");

            string? shellOutput = null;
            if (FunctionNameMatches(name, "shell_run") && resultText.Length > 0)
            {
                const int MaxEventShellOutput = 500;
                shellOutput = resultText.Length > MaxEventShellOutput
                    ? resultText[..MaxEventShellOutput] + $"…[{resultText.Length - MaxEventShellOutput} chars truncated]"
                    : resultText;
            }

            string? toolError = null;
            if (!succeeded && !FunctionNameMatches(name, "shell_run") && resultText.Length > 0)
            {
                const int MaxEventError = 300;
                toolError = resultText.Length > MaxEventError
                    ? resultText[..MaxEventError] + $"…[{resultText.Length - MaxEventError} chars truncated]"
                    : resultText;
            }

            _ = _eventEmitter.EmitAsync(EventTypes.ToolCall,
                agent:   agentName,
                payload: new { tool = name, arg, ok = succeeded, result_chars = resultText.Length, output = shellOutput, error = toolError });

            // Emit typed outcome event alongside the generic tool_call.
            if (resultText.StartsWith("[TIMEOUT]", StringComparison.Ordinal))
                _ = _eventEmitter.EmitAsync(EventTypes.ToolTimeout,
                    agent:   agentName,
                    payload: new { tool = name, arg });
            else if (!succeeded)
                _ = _eventEmitter.EmitAsync(EventTypes.ToolError,
                    agent:   agentName,
                    payload: new { tool = name, arg, error = toolError });
            else
                _ = _eventEmitter.EmitAsync(EventTypes.ToolResult,
                    agent:   agentName,
                    payload: new { tool = name, arg, result_chars = resultText.Length });
        }

        // Intercept search_symbol results to populate SymbolDefinition evidence nodes.
        // Read-only — goes only to the evidence graph, never the flat change log.
        if (_evidenceStore is not null
            && succeeded
            && SymbolTrackedSubstrings.Any(s => FunctionNameMatches(name, s)))
        {
            var sym = OrchestratorHelpers.GetArg(context.Arguments, "symbol") ?? string.Empty;
            _symbolPending.Enqueue(new SymbolSearchRecord(sym, resultText));
        }

        // Intercept search_callers results to populate SymbolReference evidence nodes.
        // Read-only — same path as search_symbol but produces reference nodes instead of
        // definition nodes; TargetFile is resolved from existing SymbolDefinition nodes at flush.
        if (_evidenceStore is not null
            && succeeded
            && CallerTrackedSubstrings.Any(s => FunctionNameMatches(name, s)))
        {
            var sym = OrchestratorHelpers.GetArg(context.Arguments, "symbol") ?? string.Empty;
            _callerPending.Enqueue(new CallerSearchRecord(sym, resultText));
        }

        // Only enqueue state-changing calls to _pending — these feed changes.json and are
        // used by routing validators (RequireShellPass, RequireAllFilesWritten, etc.).
        // Read-only calls (read_file, list_files, search_content) are visible in events
        // but do not belong in the change log.
        if (!isTracked)
            return result;

        // Capture shell output for post-hoc verification by routing validators.
        // Capture for ALL shell_run calls (succeeded or not) so that commands which are
        // expected to exit non-zero (e.g. exit-code tests) can still be fingerprint-matched
        // by HandoffToReviewerValidator check 8.
        // Cap at 4 096 chars so changes.json stays compact even for verbose test suites.
        string? output = null;
        if (FunctionNameMatches(name, "shell_run"))
        {
            const int MaxOutputBytes = 4096;
            output = resultText.Length > MaxOutputBytes
                ? resultText[..MaxOutputBytes]
                : resultText;
        }

        _pending.Enqueue(new InvocationRecord(name, context.Arguments, succeeded, output));
        return result;
    }

}

