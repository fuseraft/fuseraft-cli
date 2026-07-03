using System.ComponentModel;
using System.Text;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives REPL agents first-class access to session metadata, the event log, and
/// diagnostic log files so they can self-inspect and distinguish the current
/// session from previous ones.
/// </summary>
public sealed class ReplSessionPlugin(
    string sessionId,
    DateTime startedAt,
    string modelId,
    string cwd)
{
    // Wired by ReplCommand after context construction so the agent can trigger compaction
    // and query live context state without a circular construction dependency.
    private Func<string?, CancellationToken, Task<string>>?        _compactDelegate;
    private Func<(int EstimatedTokens, int Budget, int TurnIndex)>? _statusDelegate;

    internal void SetCompactDelegate(Func<string?, CancellationToken, Task<string>> compact)
        => _compactDelegate = compact;

    internal void SetStatusDelegate(Func<(int EstimatedTokens, int Budget, int TurnIndex)> status)
        => _statusDelegate = status;

    [Description(
        "Compact the conversation history into a concise handoff summary to free context budget. " +
        "Call this when accumulated previous turns or tool reads are consuming most of the context window — " +
        "the agent keeps seeing budget-exceeded errors or context is near the 80k token ceiling. " +
        "The compaction takes effect immediately: the next turn starts with the compact summary instead of the full history. " +
        "Safe to call at any point in the session.")]
    public Task<string> CompactContextAsync(
        [Description("Optional one-line focus for the summary (e.g. 'fix build error in SharePointClient.cs'). " +
                     "Helps the summary emphasise the most relevant prior context.")] string? focus = null,
        CancellationToken cancellationToken = default) =>
        _compactDelegate is not null
            ? _compactDelegate(focus, cancellationToken)
            : Task.FromResult(PluginResult.Error("Compaction is not available in this session."));

    [Description(
        "Returns the current context budget: estimated token count, budget ceiling, percentage used, remaining tokens, and turn index. " +
        "Call this before starting a multi-file investigation, or any time you want to know how much headroom " +
        "remains before deciding whether to call compact_context.")]
    public string GetContextStatus()
    {
        if (_statusDelegate is null)
            return PluginResult.Error("Context status is not available in this session.");

        var (estimated, budget, turn) = _statusDelegate();
        var pct       = (double)estimated / budget;
        var remaining = budget - estimated;
        return $"estimated_tokens: {estimated:N0}\n" +
               $"budget:           {budget:N0}\n" +
               $"pct_used:         {pct:P1}\n" +
               $"tokens_remaining: {remaining:N0}\n" +
               $"turn:             {turn}";
    }

    [Description("Get metadata for the current REPL session: ID, model, start time, working dir, snapshot path, and log file locations.")]
    public string Current()
    {
        var snapshotPath = Path.Combine(FuseraftPaths.GlobalReplSessions, $"repl-{sessionId}.json");

        var sb = new StringBuilder();
        sb.AppendLine($"Session ID:   {sessionId}");
        sb.AppendLine($"Started:      {startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Model:        {modelId}");
        sb.AppendLine($"Working dir:  {cwd}");
        sb.AppendLine($"Snapshot:     {snapshotPath}");
        sb.AppendLine();
        var slug = FuseraftPaths.ProjectSlug(cwd);
        sb.AppendLine("Log files:");
        sb.AppendLine($"  repl_events     {FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsLog, slug)}");
        sb.AppendLine($"  events          {FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalEventsLog, sessionId, slug)}");
        sb.AppendLine($"  provider_errors {FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalProviderErrors, slug)}");
        sb.AppendLine($"  app             {FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAppLog, slug)}");
        return sb.ToString().TrimEnd();
    }

    [Description("List all saved REPL sessions, newest first. The current session is marked.")]
    public async Task<string> ListAsync()
    {
        var sessions = await ReplSessionSnapshot.ListAsync();
        if (sessions.Count == 0)
            return PluginResult.Info("No saved sessions found.");

        var sb = new StringBuilder();
        sb.AppendLine($"{"ID",-14}  {"Started",-19}  {"Updated",-19}  {"Turns",5}  {"Model"}");
        sb.AppendLine(new string('-', 88));
        foreach (var s in sessions)
        {
            var marker = s.SessionId == sessionId ? " ◄ current" : "";
            sb.AppendLine(
                $"{s.SessionId,-14}  " +
                $"{s.StartedAt.ToLocalTime(),-19:yyyy-MM-dd HH:mm:ss}  " +
                $"{s.LastUpdatedAt.ToLocalTime(),-19:yyyy-MM-dd HH:mm:ss}  " +
                $"{s.TurnIndex,5}  {s.ModelId}{marker}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Read the REPL event log for a session. Defaults to the current session.")]
    public async Task<string> ReadEventLogAsync(
        [Description("Session ID to filter by. Leave empty to use the current session.")] string? targetSessionId = null,
        [Description("Maximum number of events to return (most recent).")] int maxLines = 50)
    {
        var filter = string.IsNullOrWhiteSpace(targetSessionId) ? sessionId : targetSessionId.Trim();
        var path = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsLog, FuseraftPaths.ProjectSlug(cwd));
        if (!File.Exists(path))
            return PluginResult.Info($"No REPL event log at {path}. The log is created on first session activity.");

        var allLines = await File.ReadAllLinesAsync(path);
        var matching = allLines
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains($"\"{filter}\""))
            .TakeLast(Math.Max(1, maxLines))
            .ToList();

        if (matching.Count == 0)
            return PluginResult.Info($"No events found for session '{filter}' in {path}.");

        return string.Join("\n", matching);
    }

    [Description("Read a diagnostic log file. Valid names: repl_events, events, provider_errors, app.")]
    public async Task<string> ReadLogAsync(
        [Description("Log name: repl_events, events, provider_errors, or app.")] string logName = "repl_events",
        [Description("Maximum number of lines to return (from end of file).")] int maxLines = 100)
    {
        var slug = FuseraftPaths.ProjectSlug(cwd);
        var path = logName.ToLowerInvariant() switch
        {
            "repl_events"     => FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsLog, slug),
            "events"          => FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalEventsLog, sessionId, slug),
            "provider_errors" => FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalProviderErrors, slug),
            "app"             => FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAppLog, slug),
            _ => null,
        };

        if (path is null)
            return PluginResult.Error(
                $"Unknown log '{logName}'. Valid names: repl_events, events, provider_errors, app.");

        if (!File.Exists(path))
            return PluginResult.Info($"Log file not found: {path}");

        var lines = await File.ReadAllLinesAsync(path);
        var tail  = lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(Math.Max(1, maxLines)).ToList();
        return string.Join("\n", tail);
    }
}
