using Microsoft.Extensions.AI;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Knowledge;
using fuseraft.Core.Models.Orchestration;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Context;

namespace fuseraft.Cli.Protocol;

// Wire records are deliberately separate from the internal types they're mapped from: renaming or
// reshaping an internal model must not change the JSON clients parse. Add fields freely; removing or
// renaming one is a breaking change (see WireProtocol.Version).

/// <summary>An orchestration session from <c>fuseraft sessions --json</c>.</summary>
public sealed record WireSession(
    string SessionId, string Task, string? WorkingDirectory, string? ConfigPath,
    DateTime StartedAt, DateTime LastUpdatedAt, bool IsComplete, int TurnCount)
{
    public static WireSession From(SessionIndexEntry e) => new(
        e.SessionId, e.Task, e.WorkingDirectory, e.ConfigPath,
        e.StartedAt, e.LastUpdatedAt, e.IsComplete, e.TurnCount);
}

/// <summary>An orchestration session with its messages, from <c>fuseraft sessions --show &lt;id&gt; --json</c>.</summary>
public sealed record WireSessionDetail(
    string SessionId, string Task, string? WorkingDirectory, string ConfigPath,
    DateTime StartedAt, DateTime LastUpdatedAt, bool IsComplete, IReadOnlyList<WireAgentMessage> Messages)
{
    public static WireSessionDetail From(SessionCheckpoint c) => new(
        c.SessionId, c.Task, c.WorkingDirectory, c.ConfigPath, c.StartedAt, c.LastUpdatedAt, c.IsComplete,
        [.. c.Messages.Select(WireAgentMessage.From)]);
}

/// <summary>
/// One orchestration message — in a session's history and in <c>fuseraft run --bridge</c>'s
/// <c>agent_message</c> event. <c>Role</c> is <c>assistant</c> for an agent's turn and <c>user</c> for
/// injected text (a human redirect, or an <c>Orchestrator</c> correction).
/// </summary>
public sealed record WireAgentMessage(
    string AgentName, string Role, string Content, int TurnIndex, DateTime Timestamp,
    int? InputTokens, int? OutputTokens, bool IsCompactionSummary, IReadOnlyList<WireToolCall> ToolCalls)
{
    public static WireAgentMessage From(AgentMessage m) => new(
        m.AgentName, m.Role, m.Content, m.TurnIndex, m.Timestamp,
        m.Usage?.InputTokens, m.Usage?.OutputTokens, m.IsCompactionSummary,
        [.. (m.ToolCalls ?? []).Select(t => new WireToolCall(t.Name, t.ArgsSummary, t.Succeeded))]);
}

public sealed record WireToolCall(string Name, string? ArgsSummary, bool Succeeded);

/// <summary>A config file from <c>fuseraft config --list --json</c>; <c>Error</c> is set when it doesn't load.</summary>
public sealed record WireConfigSummary(
    string Path, string? Name, string? Description, IReadOnlyList<string> Agents, string? SelectionType, string? Error)
{
    public static WireConfigSummary From(string path, OrchestrationConfig c) => new(
        System.IO.Path.GetFullPath(path), c.Name, c.Description,
        [.. c.Agents.Select(a => a.Name)], c.Selection.Type, null);

    public static WireConfigSummary Failed(string path, string error) =>
        new(System.IO.Path.GetFullPath(path), null, null, [], null, error);
}

/// <summary>
/// The result of <c>fuseraft validate --json</c>. <c>Valid</c> is false when any issue's <c>Level</c> is
/// <c>error</c>; <c>Config</c> is null when the file didn't load far enough to summarise.
/// </summary>
public sealed record WireValidation(string Path, bool Valid, IReadOnlyList<WireIssue> Issues, WireConfigSummary? Config)
{
    public static WireValidation From(
        string path, IEnumerable<(string Level, string Message)> issues, OrchestrationConfig? config)
    {
        var list = issues.Select(i => new WireIssue(i.Level, i.Message)).ToList();
        return new(System.IO.Path.GetFullPath(path), list.All(i => i.Level != "error"), list,
            config is null ? null : WireConfigSummary.From(path, config));
    }
}

/// <summary>A validation finding; <c>Level</c> is <c>error</c> or <c>warning</c>.</summary>
public sealed record WireIssue(string Level, string Message);

/// <summary>A team template from <c>fuseraft init --list-templates --json</c>.</summary>
public sealed record WireTemplate(string Key, string Label, string Description);

/// <summary>The files <c>fuseraft init --json</c> wrote, as absolute paths.</summary>
public sealed record WireInitResult(
    string Template, string Model, string? Endpoint, string ConfigPath, IReadOnlyList<string> AgentFiles);

/// <summary>An agent with stored memories, from <c>fuseraft memory agents --json</c>.</summary>
public sealed record WireMemoryAgent(string Agent, int Count);

/// <summary>A REPL chat from <c>fuseraft sessions --repl --json</c>.</summary>
public sealed record WireReplSession(
    string SessionId, string ModelId, string Cwd, string? Title, string? AutoTitle,
    DateTime StartedAt, DateTime LastUpdatedAt, int TurnIndex, string FirstUserMessage)
{
    internal const int MaxPreviewChars = 500;

    public static WireReplSession From(ReplSessionSnapshot s) => new(
        s.SessionId, s.ModelId, s.Cwd,
        string.IsNullOrWhiteSpace(s.Title) ? null : s.Title,
        string.IsNullOrWhiteSpace(s.AutoTitle) ? null : s.AutoTitle,
        s.StartedAt, s.LastUpdatedAt, s.TurnIndex, FirstUserText(s));

    private static string FirstUserText(ReplSessionSnapshot s)
    {
        var text = s.History.FirstOrDefault(m => m.Role == ChatRole.User.Value)?
            .Contents.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
        return text.Length <= MaxPreviewChars ? text : text[..MaxPreviewChars];
    }
}

/// <summary>A memory from <c>fuseraft memory list --json</c>.</summary>
public sealed record WireMemory(string Guid, string Name, string Type, string Description, string Body)
{
    public static WireMemory From(MemoryEntry e) => new(e.Guid, e.Name, e.Type, e.Description, e.Body);
}

/// <summary>An imported context item from <c>fuseraft context list --json</c>.</summary>
public sealed record WireContextItem(
    string Name, string? Description, string SourcePath, DateTime ImportedAt,
    long SizeBytes, IReadOnlyList<WireContextFile> Files)
{
    public static WireContextItem From(ContextItem i) => new(
        i.Name, i.Description, i.SourcePath, i.ImportedAt, i.Files.Sum(f => f.SizeBytes),
        [.. i.Files.Select(f => new WireContextFile(f.RelativePath, f.SizeBytes))]);
}

public sealed record WireContextFile(string RelativePath, long SizeBytes);

/// <summary>A scheduled job from <c>fuseraft schedule list --json</c>.</summary>
public sealed record WireScheduledJob(
    string Name, string? Description, string Cron, string Task, string? Config, string? WorkDir,
    bool Enabled, bool Due, DateTimeOffset CreatedAt, DateTimeOffset? LastRun, DateTimeOffset? NextRun)
{
    public static WireScheduledJob From(ScheduledJob j, DateTimeOffset now) => new(
        j.Name, j.Description, j.Cron, j.Task, j.Config, j.WorkDir,
        j.Enabled, j.Enabled && j.NextRun <= now, j.CreatedAt, j.LastRun, j.NextRun);
}

/// <summary>A plugin from <c>fuseraft plugins --json</c>; <c>Source</c> is <c>builtin</c> or <c>mcp</c>.</summary>
public sealed record WirePlugin(string Name, string Source, IReadOnlyList<WireFunction> Functions)
{
    public static WirePlugin From(string name, string source, IEnumerable<AIFunction> functions) => new(
        name, source, [.. functions.Select(f => new WireFunction(f.Name, f.Description ?? string.Empty))]);
}

public sealed record WireFunction(string Name, string Description);

/// <summary>A model from <c>fuseraft models --json</c>.</summary>
public sealed record WireModel(string Id, bool Current);
