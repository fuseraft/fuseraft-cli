using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;


/// <summary>
/// Automatically records every tool call made by any agent into a structured JSON log
/// on disk (<c>.fuseraft/changes.json</c> by default).
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

    public ChangeTracker(string logPath, EventEmitter? eventEmitter = null, EvidenceStore? evidenceStore = null, IntentLog? intentLog = null)
    {
        _logPath        = logPath;
        _eventEmitter   = eventEmitter;
        _evidenceStore  = evidenceStore;
        _intentLog      = intentLog;
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
                    await Console.Error.WriteLineAsync($"[fuseraft] ChangeTracker: failed to load '{_logPath}': {ex.Message} — change log reset.");
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

        if (records.Count == 0) return;

        var entry = new ChangeEntry
        {
            Agent     = agentName,
            TurnIndex = turnIndex,
            Timestamp = DateTime.UtcNow,
            SessionId = _sessionId,

            FilesWritten = [.. records
                .Where(r => (FunctionNameMatches(r.Name, "write_file") || FunctionNameMatches(r.Name, "patch_file")) && r.Succeeded)
                .Select(r => GetArg(r.Args, "path"))
                .Concat(records
                    .Where(r => FunctionNameMatches(r.Name, "copy_file") && r.Succeeded)
                    .Select(r => GetArg(r.Args, "destination")))
                .Concat(records
                    .Where(r => FunctionNameMatches(r.Name, "move_file") && r.Succeeded)
                    .Select(r => GetArg(r.Args, "destination")))
                .OfType<string>()],

            FilesDeleted = [.. records
                .Where(r => FunctionNameMatches(r.Name, "delete_file") && r.Succeeded)
                .Select(r => GetArg(r.Args, "path"))
                .Concat(records
                    .Where(r => FunctionNameMatches(r.Name, "delete_directory") && r.Succeeded)
                    .Select(r => GetArg(r.Args, "path")))
                .Concat(records
                    .Where(r => FunctionNameMatches(r.Name, "move_file") && r.Succeeded)
                    .Select(r => GetArg(r.Args, "source")))
                .OfType<string>()],

            CommandsRun = [.. records
                .Where(r => FunctionNameMatches(r.Name, "shell_run"))
                .Select(r => new CommandRecord
                {
                    Command   = GetArg(r.Args, "command") ?? GetArg(r.Args, "script") ?? "(script)",
                    Succeeded = r.Succeeded,
                    Output    = r.Output
                })],

            GitCommits = [.. records
                .Where(r => FunctionNameMatches(r.Name, "git_commit") && r.Succeeded)
                .Select(r => GetArg(r.Args, "message"))
                .OfType<string>()]
        };

        if (!entry.FilesWritten.Any() && !entry.FilesDeleted.Any() &&
            !entry.CommandsRun.Any()  && !entry.GitCommits.Any())
            return;

        // Emit typed evidence nodes for the evidence graph (alongside flat changes.json).
        if (_evidenceStore is not null)
            await EmitEvidenceNodesAsync(agentName, turnIndex, records, cancellationToken);

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
                    await Console.Error.WriteLineAsync($"[fuseraft] ChangeTracker: failed to load '{_logPath}' during flush: {ex.Message} — change log reset.");
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
            var path = GetArg(r.Args, "destination") // copy_file / move_file use "destination"
                    ?? GetArg(r.Args, "path");
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
                ? GetArg(r.Args, "source")
                : GetArg(r.Args, "path");
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
            var command = GetArg(r.Args, "command") ?? GetArg(r.Args, "script") ?? "(script)";
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
            var message = GetArg(r.Args, "message");
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
            var arg = GetArg(context.Arguments, "path")
                   ?? GetArg(context.Arguments, "source")
                   ?? GetArg(context.Arguments, "destination")
                   ?? GetArg(context.Arguments, "command")
                   ?? GetArg(context.Arguments, "script")
                   ?? GetArg(context.Arguments, "message")
                   ?? GetArg(context.Arguments, "directory")
                   ?? GetArg(context.Arguments, "query");

            string? shellOutput = null;
            if (FunctionNameMatches(name, "shell_run") && resultText.Length > 0)
            {
                const int MaxEventShellOutput = 500;
                shellOutput = resultText.Length > MaxEventShellOutput
                    ? resultText[..MaxEventShellOutput] + $"…[{resultText.Length - MaxEventShellOutput} chars truncated]"
                    : resultText;
            }

            _ = _eventEmitter.EmitAsync("tool_call",
                agent:   agentName,
                payload: new { tool = name, arg, ok = succeeded, output = shellOutput });
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

    // Helpers

    private static string? GetArg(IReadOnlyDictionary<string, object?>? args, string key)
    {
        if (args is null) return null;
        if (!args.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }
}

/// <summary>In-memory snapshot of one completed function invocation.</summary>
public sealed record InvocationRecord(
    string Name,
    IReadOnlyDictionary<string, object?>? Args,
    bool Succeeded,
    string? Output = null);
