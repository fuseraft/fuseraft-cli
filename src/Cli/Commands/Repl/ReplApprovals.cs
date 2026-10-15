using System.Text.Json;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// "Always" answers to the REPL's HITL approvals, and what each one covers:
/// <list type="bullet">
/// <item>A shell command: commands using the same programs. Saved for the workspace, so later sessions don't ask
/// again, unless one of them can run any code (a shell, an interpreter, <c>eval</c>), which lasts this session only.
/// Not offered when the command can't be read with confidence, sets environment variables first, or runs a program
/// by path.</item>
/// <item>A file write inside the workspace: all file writes there, this session only, since seeing each diff is the
/// point of asking.</item>
/// <item>A FileSystem action (delete, move, copy, ...) inside the workspace: every call to that action, this session
/// only, for the same reason.</item>
/// <item>A Git or Http action: every call to that tool (<c>git_commit</c>, <c>http_post</c>), saved for the
/// workspace.</item>
/// </list>
/// Never offered, and never applied, to a sandbox-escape grant, which is a decision of its own.
/// </summary>
internal sealed class ReplApprovals(ReplApprovalStore? store, string workspace)
{
    /// <summary>The note on an "always" label that will be saved for the workspace.</summary>
    internal const string SavedNote = " (saved for this directory)";

    static readonly HashSet<string> RunsAnyCodePrograms = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh", "fish", "python", "python3", "perl", "ruby", "node", "php", "deno", "bun",
        "cmd", "powershell", "pwsh", "eval", "source", ".", "env", "xargs", "exec", "npx", "bunx", "uvx",
    };

    readonly Lock _lock = new();
    readonly HashSet<string> _programs = new(store?.Load().Programs ?? [], StringComparer.Ordinal);
    readonly HashSet<string> _tools = new(store?.Load().Tools ?? [], StringComparer.Ordinal);
    readonly HashSet<string> _sessionActions = new(StringComparer.Ordinal);
    bool _writes;

    /// <summary>The store saved answers go to, for /approvals.</summary>
    public ReplApprovalStore? Store => store;

    /// <summary>What "always" covers now: saved programs and tools, and what lasts only this session.</summary>
    public (IReadOnlyList<string> Programs, IReadOnlyList<string> Tools, IReadOnlyList<string> Session) Describe()
    {
        var saved = store?.Load() ?? new ReplApprovalStore.Saved();
        lock (_lock)
        {
            List<string> session = [.. _programs.Where(p => !saved.Programs.Contains(p)).Order().Select(p => $"commands using {p}")];
            session.AddRange(_tools.Where(t => !saved.Tools.Contains(t)).Order().Select(t => $"all {t} calls"));
            session.AddRange(_sessionActions.Order().Select(a => $"all {a} calls in the workspace"));
            if (_writes) session.Add("all file writes in the workspace");
            return ([.. saved.Programs], [.. saved.Tools], session);
        }
    }

    /// <summary>Forgets every "always" answer, saved or from this session.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _programs.Clear();
            _tools.Clear();
            _sessionActions.Clear();
            _writes = false;
        }
        store?.Clear();
    }

    public async Task<bool> ShellAsync(string command, Func<string?, Task<ApprovalAnswer>> ask)
    {
        var programs = Programs(command);
        lock (_lock)
            if (programs is { Count: > 0 } && programs.All(_programs.Contains))
                return true;
        var save = store is not null && programs is { Count: > 0 } && !programs.Any(RunsAnyCodePrograms.Contains);
        var label = programs is { Count: > 0 } ? $"commands using {string.Join(", ", programs.Order())}{(save ? SavedNote : "")}" : null;
        var answer = await ask(label);
        if (answer == ApprovalAnswer.Always && programs is { Count: > 0 })
        {
            lock (_lock) _programs.UnionWith(programs);
            if (save) store!.Add(programs: programs);
        }
        return answer != ApprovalAnswer.No;
    }

    public async Task<bool> FileWriteAsync(string path, Func<string?, Task<ApprovalAnswer>> ask)
    {
        var inside = IsInside(path);
        lock (_lock)
            if (inside && _writes)
                return true;
        var answer = await ask(inside ? "all file writes in the workspace, this session" : null);
        if (answer == ApprovalAnswer.Always && inside)
            lock (_lock) _writes = true;
        return answer != ApprovalAnswer.No;
    }

    public async Task<bool> ToolActionAsync(string plugin, string action, string detail, Func<string?, Task<ApprovalAnswer>> ask)
    {
        // A sandbox escape comes through the same callback; "always" never covers one.
        if (detail.Contains(IncludedRootsState.EscapeMarker, StringComparison.Ordinal))
            return await ask(null) != ApprovalAnswer.No;

        var fileSystem = plugin == "FileSystem";
        // An action's detail is a resolved path, or "src → dst" (or "path → mode"): every part has to be inside.
        var offered = !fileSystem || detail.Split(" → ").All(IsInside);
        lock (_lock)
            if (offered && (fileSystem ? _sessionActions : _tools).Contains(action))
                return true;
        var save = !fileSystem && store is not null;
        var label = !offered ? null
            : fileSystem ? $"all {action} calls in the workspace, this session"
            : $"all {action} calls{(save ? SavedNote : "")}";
        var answer = await ask(label);
        if (answer == ApprovalAnswer.Always && offered)
        {
            lock (_lock) (fileSystem ? _sessionActions : _tools).Add(action);
            if (save) store!.Add(tool: action);
        }
        return answer != ApprovalAnswer.No;
    }

    /// <summary>
    /// The programs a shell command runs, for "always": every simple command's name except <c>cd</c>, which only moves
    /// the ones after it. Null when "always" can't be offered for it: the command can't be read with confidence, one of
    /// its commands sets environment variables first (<c>LD_PRELOAD=… ls</c>), or runs a program by path (<c>./ls</c>).
    /// </summary>
    internal static IReadOnlySet<string>? Programs(string command)
    {
        if (DangerousCommandDetector.EnumerateCommands(command) is not { Count: > 0 } commands)
            return null;
        if (commands.Any(c => c.HasEnvironmentAssignment || c.Text.Contains('/') || c.Text.Contains('\\')))
            return null;
        var programs = commands.Select(c => c.Name).Where(n => n != "cd").ToHashSet(StringComparer.Ordinal);
        return programs.Count > 0 ? programs : null;
    }

    bool IsInside(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path, workspace).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>
/// "Always" answers saved per workspace in <c>~/.fuseraft/approvals.json</c>: programs for shell commands, and Git and
/// Http tools. The file belongs to the user, so a cloned project can't grant itself anything.
/// </summary>
internal sealed class ReplApprovalStore(string path, string workspace)
{
    public sealed class Saved
    {
        public SortedSet<string> Programs { get; set; } = new(StringComparer.Ordinal);

        public SortedSet<string> Tools { get; set; } = new(StringComparer.Ordinal);
    }

    public static ReplApprovalStore ForWorkspace(string workspace) =>
        new(Path.Combine(FuseraftPaths.GlobalRoot, "approvals.json"), Path.GetFullPath(workspace));

    public string FilePath => path;

    public Saved Load() => Read().GetValueOrDefault(workspace) ?? new();

    /// <summary>Merges into what is on disk, so two sessions in the same directory don't overwrite each other.</summary>
    public void Add(IEnumerable<string>? programs = null, string? tool = null)
    {
        lock (Gate)
        {
            var all = Read();
            var saved = all.GetValueOrDefault(workspace) ?? new();
            saved.Programs.UnionWith(programs ?? []);
            if (tool is not null)
                saved.Tools.Add(tool);
            all[workspace] = saved;
            Write(all);
        }
    }

    public void Clear()
    {
        lock (Gate)
        {
            var all = Read();
            if (all.Remove(workspace))
                Write(all);
        }
    }

    static readonly Lock Gate = new();

    Dictionary<string, Saved> Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    void Write(Dictionary<string, Saved> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An answer that can't be saved still holds for this session.
        }
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
