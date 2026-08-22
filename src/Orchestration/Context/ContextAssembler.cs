using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Context;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration.Context;

/// <summary>
/// Assembles agent context and handoff blocks from durable disk artifacts rather than
/// replaying the shared session transcript.
///
/// <para>
/// Two entry points serve distinct purposes:
/// <list type="bullet">
///   <item><see cref="ResolveAsync"/> — called by the state machine when a transition fires.
///     Returns a formatted string injected into history as a handoff context block.</item>
///   <item><see cref="AssembleForAgentAsync"/> — called by the orchestrator at agent
///     invocation time when an agent declares <c>AgentConfig.Context</c>. Returns a
///     <see cref="ChatMessage"/> list that replaces shared-history replay entirely, giving
///     the agent only the artifacts it needs plus its own prior turns.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ContextAssembler
{
    private readonly string? _sandboxRoot;
    private readonly string? _changeLogPath;
    private readonly string? _briefPath;
    private readonly string? _executionStatePath;
    private readonly string? _investigationLogPath;
    private readonly RepositoryGraphStore? _graphStore;
    private readonly AdrRegistry? _adrRegistry;
    private readonly fuseraft.Infrastructure.Objectives.ObjectiveManager? _objectiveManager;
    private readonly ContextBroker? _contextBroker;

    private string _sessionId = string.Empty;

    private const int DefaultMaxCharsPerSource   = 4_000;
    // Own-history default is higher than artifact sources because each turn naturally
    // contains more text, but still bounded so 4 verbose turns don't silently cost 80k chars.
    private const int DefaultMaxCharsOwnHistory  = 8_000;

    private const int TaskReminderMinContextChars = 2_000;
    private const int TaskReminderMinTaskLength   = 50;
    private const int TaskReminderPreviewChars    = 200;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public ContextAssembler(
        string? sandboxRoot           = null,
        string? changeLogPath         = null,
        string? briefPath             = null,
        RepositoryGraphStore? graphStore       = null,
        AdrRegistry?          adrRegistry      = null,
        fuseraft.Infrastructure.Objectives.ObjectiveManager? objectiveManager = null,
        ContextBroker?        contextBroker    = null,
        string? executionStatePath    = null,
        string? investigationLogPath  = null)
    {
        _sandboxRoot          = sandboxRoot;
        _changeLogPath        = changeLogPath;
        _briefPath            = briefPath;
        _executionStatePath   = executionStatePath;
        _investigationLogPath = investigationLogPath;
        _graphStore           = graphStore;
        _adrRegistry          = adrRegistry;
        _objectiveManager     = objectiveManager;
        _contextBroker        = contextBroker;
    }

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Returns the current session context summary, or <c>null</c> when the file does not
    /// exist or is empty. Used by orchestrators to auto-inject context for agents that do not
    /// declare an explicit <c>Context</c> spec.
    /// </summary>
    public Task<string?> ReadSessionContextAsync(CancellationToken ct = default)
        => ResolveSessionContextAsync(DefaultMaxCharsPerSource, ct);

    // ── Handoff injection (state machine transitions) ────────────────────────

    /// <summary>
    /// Resolves <paramref name="sources"/> into a formatted text block labelled for
    /// <paramref name="toAgent"/>. The result is injected into shared history as a user
    /// message after the turn-boundary marker when a transition fires.
    /// Returns <c>null</c> when no source yields content.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string toAgent,
        IReadOnlyList<ContextSource> sources,
        CancellationToken ct = default)
    {
        if (sources.Count == 0) return null;

        var sections = new List<(string Label, string Content)>(sources.Count);
        foreach (var src in sources)
        {
            // own_history is only meaningful in AssembleForAgentAsync; skip it here.
            var (type, _) = ParseSource(src.Source);
            if (type == "own_history") continue;

            var content = await ResolveArtifactAsync(src, ct);
            if (!string.IsNullOrWhiteSpace(content))
                sections.Add((src.Label ?? DefaultLabel(src.Source), content.Trim()));
        }

        if (sections.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"[HANDOFF CONTEXT — assembled for {toAgent}]");
        foreach (var (label, content) in sections)
        {
            sb.AppendLine();
            sb.AppendLine($"## {label}");
            sb.AppendLine(content);
        }
        return sb.ToString().TrimEnd();
    }

    // ── Per-agent context assembly (replaces ContextWindowFilter) ────────────

    /// <summary>
    /// Assembles the full context for an agent invocation from <paramref name="sources"/>,
    /// replacing shared-history replay. The returned list is a drop-in replacement for the
    /// output of <c>ContextWindowFilter.Apply</c>.
    ///
    /// <para>Layout (in order):</para>
    /// <list type="number">
    ///   <item>The original task message (always first, so the agent knows its goal).</item>
    ///   <item>The agent's own prior turns from <paramref name="sharedHistory"/>
    ///     (from any <c>own_history:N</c> source), text-only, oldest first.</item>
    ///   <item>A single user message containing all resolved artifact sources
    ///     (session context, change log, brief fields, files).</item>
    /// </list>
    /// </summary>
    public async Task<AgentContextAssembly> AssembleForAgentAsync(
        string agentName,
        string task,
        IReadOnlyList<ContextSource> sources,
        IList<ChatMessage> sharedHistory,
        AgentDirective? directive = null,
        CancellationToken ct = default)
    {
        var result = new List<ChatMessage>();
        var emptySources = new List<string>();

        // 1. Task message — the agent always needs to know what it's working on. When a
        // synthesized directive is available (handoff() goal/background/constraints), it
        // replaces the raw task string — this is what makes Fresh isolation self-contained
        // rather than just "an empty Context: block with no explanation of what to do".
        result.Add(new ChatMessage(ChatRole.User, directive?.Format() ?? task));

        // Separate own_history sources from artifact sources.
        ContextSource? ownHistorySrc = null;
        var artifactSources = new List<ContextSource>(sources.Count);
        foreach (var src in sources)
        {
            var (type, _) = ParseSource(src.Source);
            if (type == "own_history") ownHistorySrc = src;
            else                       artifactSources.Add(src);
        }

        // 2. Agent's own prior turns (text-only, chronological, char-bounded).
        if (ownHistorySrc is not null)
        {
            var (_, param) = ParseSource(ownHistorySrc.Source);
            var n        = int.TryParse(param, out var parsed) ? Math.Max(1, parsed) : 6;
            var maxChars = ownHistorySrc.MaxChars > 0 ? ownHistorySrc.MaxChars : DefaultMaxCharsOwnHistory;
            var ownTurns = ExtractOwnHistory(agentName, n, maxChars, sharedHistory);
            result.AddRange(ownTurns);
        }

        // 3. Artifact block — all non-own_history sources formatted into one user message.
        if (artifactSources.Count > 0)
        {
            var sections = new List<(string Label, string Content)>(artifactSources.Count);
            foreach (var src in artifactSources)
            {
                var content = await ResolveArtifactAsync(src, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    sections.Add((src.Label ?? DefaultLabel(src.Source), content.Trim()));
                else
                    emptySources.Add(src.Source);
            }

            if (sections.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[AGENT CONTEXT — assembled from artifacts]");
                foreach (var (label, content) in sections)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## {label}");
                    sb.AppendLine(content);
                }
                result.Add(new ChatMessage(ChatRole.User, sb.ToString().TrimEnd()));
            }
        }

        // 4. Pending corrections — user correction messages injected into shared history after
        // this agent's last turn. Context-spec agents replace shared-history replay entirely,
        // so corrections written to shared history (by CorrectionEngine, routing strategies,
        // or the verifier hook) would otherwise be invisible on the next invocation. Re-inject
        // them here so the agent always sees the most recent feedback addressed to it.
        var pendingCorrections = ExtractPendingCorrections(agentName, sharedHistory);
        result.AddRange(pendingCorrections);

        // 5. Repeat task at recency end — exploits primacy+recency sandwich for long contexts.
        int charsAfterTask = result.Skip(1).Sum(m => m.Text?.Length ?? 0);
        if (task.Length > TaskReminderMinTaskLength && charsAfterTask > TaskReminderMinContextChars)
        {
            var preview = task.Length > TaskReminderPreviewChars ? task[..TaskReminderPreviewChars] + "…" : task;
            result.Add(new ChatMessage(ChatRole.User, $"[Task Reminder]\n\n{preview}"));
        }

        return new AgentContextAssembly(result, emptySources);
    }

    // ── Shared source resolution ─────────────────────────────────────────────

    private async Task<string?> ResolveArtifactAsync(ContextSource src, CancellationToken ct)
    {
        var maxChars = src.MaxChars > 0 ? src.MaxChars : DefaultMaxCharsPerSource;
        var (type, param) = ParseSource(src.Source);
        return type switch
        {
            "session_context"   => await ResolveSessionContextAsync(maxChars, ct),
            "changes_recent"    => await ResolveChangesRecentAsync(
                                       int.TryParse(param, out var n) ? Math.Max(1, n) : 3,
                                       maxChars, ct),
            "brief_field"       => await ResolveBriefFieldAsync(param ?? string.Empty, maxChars, ct),
            "file"              => await ResolveFileAsync(param ?? string.Empty, maxChars, ct),
            "adr_graph"         => await ResolveAdrGraphAsync(maxChars, ct),
            "active_objectives" => await ResolveActiveObjectivesAsync(maxChars, ct),
            "broker"            => await ResolveBrokerAsync(param ?? string.Empty, maxChars, ct),
            "execution_state"   => await ResolveExecutionStateAsync(maxChars, ct),
            "investigation_log" => await ResolveInvestigationLogAsync(maxChars, ct),
            _                   => null,
        };
    }

    private async Task<string?> ResolveExecutionStateAsync(int maxChars, CancellationToken ct)
    {
        if (_executionStatePath is null || !File.Exists(_executionStatePath)) return null;
        try
        {
            var json  = await File.ReadAllTextAsync(_executionStatePath, ct);
            var state = JsonSerializer.Deserialize<ExecutionState>(json, JsonOpts);
            if (state is null) return null;
            return Truncate(FormatExecutionState(state), maxChars);
        }
        catch { return null; }
    }

    private static string FormatExecutionState(ExecutionState state)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(state.Build.Command))
        {
            var status = state.Build.Succeeded
                ? "PASSED"
                : $"FAILED (exit {state.Build.ExitCode})";
            sb.AppendLine($"**Build:** {status} — `{state.Build.Command}`");

            if (!state.Build.Succeeded && state.ActiveFailures.Count > 0)
            {
                sb.AppendLine("**Errors:**");
                foreach (var f in state.ActiveFailures.Take(10))
                {
                    var loc  = f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;
                    var code = string.IsNullOrEmpty(f.Code) ? string.Empty : $"{f.Code} ";
                    sb.AppendLine($"- {code}{loc} — {f.Message}");
                }
            }
        }
        else
        {
            sb.AppendLine("**Build:** no build recorded yet");
        }

        if (state.FailedAttempts.Count > 0)
        {
            var recent = state.FailedAttempts.TakeLast(3).ToList();
            sb.AppendLine($"**Failed Attempts (last {recent.Count}):**");
            for (int i = 0; i < recent.Count; i++)
            {
                var a       = recent[i];
                var summary = a.ErrorSummary is not null ? $" → {a.ErrorSummary}" : string.Empty;
                sb.AppendLine($"{i + 1}. {a.Description}{summary}");
            }
        }

        if (state.OpenTasks.Count > 0)
        {
            sb.AppendLine("**Open Tasks:**");
            foreach (var t in state.OpenTasks)
                sb.AppendLine($"- [ ] {t.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> ResolveInvestigationLogAsync(int maxChars, CancellationToken ct)
    {
        if (_investigationLogPath is null || !File.Exists(_investigationLogPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_investigationLogPath, ct);
            var log  = JsonSerializer.Deserialize<InvestigationLog>(json, JsonOpts);
            if (log is null) return null;
            return Truncate(FormatInvestigationLog(log), maxChars);
        }
        catch { return null; }
    }

    private static string FormatInvestigationLog(InvestigationLog log)
    {
        var sb = new StringBuilder();

        var open = log.Hypotheses.Where(h => h.Status == "open").ToList();
        if (open.Count > 0)
        {
            sb.AppendLine("**Open Hypotheses:**");
            foreach (var h in open)
                sb.AppendLine($"- [{h.Id}] {h.Hypothesis}");
        }

        var rejected = log.Hypotheses.Where(h => h.Status == "rejected").ToList();
        if (rejected.Count > 0)
        {
            sb.AppendLine("**Rejected Paths (do not revisit):**");
            foreach (var h in rejected)
            {
                var reason = h.RejectReason is not null ? $" — REJECTED: {h.RejectReason}" : " — REJECTED";
                sb.AppendLine($"- [{h.Id}] {h.Hypothesis}{reason}");
            }
        }

        if (log.ConfirmedRootCauses.Count > 0)
        {
            sb.AppendLine("**Confirmed Root Causes:**");
            foreach (var cause in log.ConfirmedRootCauses)
                sb.AppendLine($"- {cause}");
        }

        if (log.Investigations.Count > 0)
        {
            var recent = log.Investigations.TakeLast(3).ToList();
            sb.AppendLine($"**Recent Investigations (last {recent.Count}):**");
            foreach (var inv in recent)
                sb.AppendLine($"- {inv.Summary} → {inv.Conclusion}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> ResolveBrokerAsync(string query, int maxChars, CancellationToken ct)
    {
        if (_contextBroker is null) return null;
        try   { return await _contextBroker.ResolveAsync(query, maxChars, ct); }
        catch { return null; }
    }

    private async Task<string?> ResolveActiveObjectivesAsync(int maxChars, CancellationToken ct)
    {
        if (_objectiveManager is null) return null;
        try
        {
            var summary = await _objectiveManager.BuildActiveSummaryAsync(ct);
            return summary is null ? null : Truncate(summary, maxChars);
        }
        catch { return null; }
    }

    // Walks adr_governs edges in the repository graph for every file recently touched
    // in this session. Returns a formatted block of governing ADR IDs and titles.
    private async Task<string?> ResolveAdrGraphAsync(int maxChars, CancellationToken ct)
    {
        if (_graphStore is null || _adrRegistry is null) return null;
        try
        {
            // Collect recently written files from the change log.
            var touchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logPath = _changeLogPath ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalChanges, FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
            if (File.Exists(logPath))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(logPath, ct);
                    var log = JsonSerializer.Deserialize<ChangeLog>(raw, JsonOpts);
                    if (log is not null)
                    {
                        foreach (var entry in log.Entries
                            .Where(e => string.IsNullOrEmpty(_sessionId) || e.SessionId == _sessionId)
                            .TakeLast(20))
                        {
                            foreach (var f in entry.FilesWritten)
                                touchedFiles.Add(f.Replace('\\', '/'));
                        }
                    }
                }
                catch { /* best-effort */ }
            }
            if (touchedFiles.Count == 0) return null;

            // Load the graph and find ADR nodes governing any of the touched files.
            var graph      = await _graphStore.LoadAsync(ct);
            var adrIds     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in touchedFiles)
            {
                var fileId = $"file:{filePath}";
                // Walk adr_governs edges: ADR node --adr_governs--> file/symbol node
                foreach (var edge in graph.EdgesTo(fileId, EdgeType.AdrGoverns))
                    adrIds.Add(edge.From.StartsWith("adr:") ? edge.From[4..] : edge.From);
            }
            if (adrIds.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("Governing architecture decisions for recently touched files:");
            foreach (var id in adrIds)
            {
                var entry = await _adrRegistry.GetByIdAsync(id, ct);
                if (entry is not null)
                    sb.AppendLine($"  [{entry.Id}] {entry.Title} (status: {entry.Status})");
                else
                    sb.AppendLine($"  [{id}]");
            }
            return Truncate(sb.ToString().TrimEnd(), maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveSessionContextAsync(int maxChars, CancellationToken ct)
    {
        var path = FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionContext, _sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return string.IsNullOrWhiteSpace(text) ? null : Truncate(text, maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveChangesRecentAsync(int count, int maxChars, CancellationToken ct)
    {
        var logPath = _changeLogPath ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalChanges, FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
        if (!File.Exists(logPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
            if (log is null || log.Entries.Count == 0) return null;

            var entries = log.Entries
                .Where(e => string.IsNullOrEmpty(_sessionId) || e.SessionId == _sessionId || e.SessionId is null)
                .TakeLast(count)
                .ToList();
            if (entries.Count == 0) entries = log.Entries.TakeLast(count).ToList();

            return Truncate(FormatChangeEntries(entries), maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveBriefFieldAsync(string field, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        var briefPath = _briefPath ?? FuseraftPaths.LocalBrief;
        var expanded  = FuseraftPaths.ExpandSessionId(briefPath, _sessionId);
        if (!File.Exists(expanded)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(expanded, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty(field, out var prop))
                root.TryGetProperty(field.ToLowerInvariant(), out prop);
            if (prop.ValueKind == JsonValueKind.Undefined) return null;

            var text = prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : prop.GetRawText();
            return text is null ? null : Truncate(text, maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveFileAsync(string relativePath, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var expanded = FuseraftPaths.ExpandSessionId(relativePath, _sessionId);
        var resolved = _sandboxRoot is not null
            ? Path.Combine(_sandboxRoot, expanded)
            : expanded;
        if (!File.Exists(resolved)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(resolved, ct);
            return Truncate(text, maxChars);
        }
        catch { return null; }
    }

    // ── own_history extraction ───────────────────────────────────────────────

    // Extracts the last N text-only assistant turns for agentName, then enforces a
    // total-char budget by dropping oldest turns first. If the most recent surviving
    // turn still exceeds maxChars, its text is truncated so the budget is always kept.
    private static IReadOnlyList<ChatMessage> ExtractOwnHistory(
        string agentName,
        int n,
        int maxChars,
        IList<ChatMessage> history)
    {
        // Collect all text-only turns for this agent, newest last.
        // Snapshot before iterating: `history` is the orchestrator's shared, still-live turn
        // list. A periodic agent (e.g. Verifier, EveryNTurns) can append to it concurrently
        // with the main turn loop's own context assembly, and enumerating the live list
        // across an await-free but otherwise unsynchronized read throws
        // InvalidOperationException ("Collection was modified") the instant that race lands.
        var ownTurns = new List<(ChatMessage Msg, int Chars)>();
        foreach (var msg in history.ToArray())
        {
            if (msg.Role != ChatRole.Assistant) continue;
            if (!string.Equals(msg.AuthorName, agentName, StringComparison.OrdinalIgnoreCase)) continue;

            var textContents = msg.Contents
                .OfType<TextContent>()
                .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .ToList<AIContent>();
            if (textContents.Count == 0) continue;

            var textOnly = textContents.Count == msg.Contents.Count
                ? msg
                : new ChatMessage(ChatRole.Assistant, textContents) { AuthorName = msg.AuthorName };
            var chars = textContents.OfType<TextContent>().Sum(t => t.Text?.Length ?? 0);
            ownTurns.Add((textOnly, chars));
        }

        // Step 1: keep only the last N turns.
        if (ownTurns.Count > n)
            ownTurns = ownTurns.Skip(ownTurns.Count - n).ToList();

        // Step 2: drop oldest turns until total chars fits within maxChars.
        while (ownTurns.Count > 1 && ownTurns.Sum(t => t.Chars) > maxChars)
            ownTurns.RemoveAt(0);

        // Step 3: if the single remaining turn still exceeds the budget, truncate its text.
        if (ownTurns.Count == 1 && ownTurns[0].Chars > maxChars)
        {
            var (msg, _) = ownTurns[0];
            var truncated = string.Concat(
                msg.Contents.OfType<TextContent>().Select(t => t.Text))[..maxChars]
                + $"\n[...truncated — own_history turn exceeded {maxChars:N0} char limit]";
            ownTurns[0] = (new ChatMessage(ChatRole.Assistant,
                [new TextContent(truncated)]) { AuthorName = msg.AuthorName }, maxChars);
        }

        return ownTurns.Select(t => t.Msg).ToList();
    }

    // ── Pending-correction extraction ───────────────────────────────────────

    // Returns all correction messages in shared history that appear after the last
    // assistant turn by agentName AND are addressed to agentName specifically — i.e. the
    // nearest preceding assistant turn belongs to agentName. Corrections are injected
    // immediately after the turn that triggered them (a blocked handoff, a validation
    // failure) with no explicit "addressed to" field, so once other agents have taken turns
    // since agentName last spoke (e.g. a graph loop revisits agentName later), a correction
    // meant for one of those other agents must not be attributed to agentName here.
    private static IReadOnlyList<ChatMessage> ExtractPendingCorrections(
        string agentName,
        IList<ChatMessage> history)
    {
        int lastOwnIdx = -1;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.Assistant &&
                string.Equals(history[i].AuthorName, agentName, StringComparison.OrdinalIgnoreCase))
            {
                lastOwnIdx = i;
                break;
            }
        }

        var corrections = new List<ChatMessage>();
        // Corrections immediately following agentName's own last turn (before any other
        // agent's turn intervenes) are addressed to agentName by construction.
        string? precedingAuthor = lastOwnIdx >= 0 ? agentName : null;
        for (int i = lastOwnIdx + 1; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.Assistant)
            {
                precedingAuthor = history[i].AuthorName;
                continue;
            }

            if (ContextWindowFilter.IsCorrectionMessage(history[i]) &&
                string.Equals(precedingAuthor, agentName, StringComparison.OrdinalIgnoreCase))
                corrections.Add(history[i]);
        }
        return corrections;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (string Type, string? Param) ParseSource(string source)
    {
        var idx = source.IndexOf(':');
        if (idx < 0) return (source.Trim().ToLowerInvariant(), null);
        return (source[..idx].Trim().ToLowerInvariant(), source[(idx + 1)..].Trim());
    }

    private static string DefaultLabel(string source)
    {
        var (type, param) = ParseSource(source);
        return type switch
        {
            "session_context"  => "Session Context",
            "changes_recent"   => "Recent Changes",
            "brief_field"      => $"Task: {param}",
            "file"             => param is not null ? Path.GetFileName(param) : "File",
            "adr_graph"        => "Governing ADRs",
            "active_objectives"=> "Active Objectives",
            "broker"           => string.IsNullOrEmpty(param) ? "Adaptive Context" : $"Adaptive Context: {param}",
            "execution_state"  => "Execution State",
            "investigation_log"=> "Investigation Log",
            _                  => source,
        };
    }

    private static string FormatChangeEntries(IReadOnlyList<ChangeEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"[Turn {e.TurnIndex}] {e.Agent} ({e.Timestamp:yyyy-MM-dd HH:mm} UTC)");
            if (e.FilesWritten.Count > 0)
            {
                sb.AppendLine("  Files written:");
                foreach (var f in e.FilesWritten) sb.AppendLine($"    - {f}");
            }
            if (e.FilesDeleted.Count > 0)
            {
                sb.AppendLine("  Files deleted:");
                foreach (var f in e.FilesDeleted) sb.AppendLine($"    - {f}");
            }
            if (e.CommandsRun.Count > 0)
            {
                sb.AppendLine("  Commands run:");
                foreach (var c in e.CommandsRun)
                    sb.AppendLine($"    - {c.Command} [{(c.Succeeded ? "OK" : "FAILED")}]");
            }
            if (e.GitCommits.Count > 0)
            {
                sb.AppendLine("  Git commits:");
                foreach (var g in e.GitCommits) sb.AppendLine($"    - {g}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        return text[..maxChars] +
               $"\n[...{text.Length - maxChars:N0} chars truncated — use file tool to read in full]";
    }
}
