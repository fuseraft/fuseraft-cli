using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>One group of chat tools as the tools menu shows it: a built-in category, an optional plugin, or an MCP server.</summary>
internal sealed record ToolCategoryInfo(string Name, string Description, bool Optional);

/// <summary>
/// The chat's tool categories and which tools each holds, shared by the chat (<c>/tools</c>, the panel's tools menu)
/// and <c>fuseraft settings tools</c>, which lists them without starting a chat.
/// </summary>
internal static class ReplToolCatalog
{
    // Curated default tool surface: the common, low-risk subset of FileSystem/Shell/Git
    // that covers a typical session (read, edit, search, status, commit). Everything else
    // in those three plugins — destructive ops, remote ops, and background jobs — is still
    // useful but rarer, so it moves behind the opt-in "Extended" plugin (--plugins Extended)
    // rather than shipping in every request's tool schema by default. This does not affect
    // /explore or /locate (SubagentPlugin's explorerTools), which are built from the full,
    // unfiltered lists regardless of whether Extended is enabled.
    internal static readonly HashSet<string> CoreFileSystemTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "patch_file", "get_file_summary",
        "list_files", "grep_file", "get_file_info", "create_directory",
    };

    internal static readonly HashSet<string> CoreShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell_run", "shell_run_script", "shell_get_env", "shell_set_env",
        "shell_which", "shell_get_working_directory",
    };

    internal static readonly HashSet<string> CoreGitTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "git_status", "git_diff", "git_log", "git_show", "git_branch_list",
        "git_add", "git_commit", "git_stash_list",
    };

    // ReplSessionPlugin split in two: get_context_status stays in the default tool set so the model can check its
    // headroom. compact_context doesn't: fuseraft compacts between turns on its own (retried each turn while over the
    // threshold), a compaction mid-turn can't shrink the request in flight, and models called it unprompted after
    // answering, which cost a round and repeated the answer.
    // current/list/read_event_log/read_log let the model enumerate and read a *different*
    // session's full event log by ID/prefix match — real cross-session data exposure with no
    // turn-to-turn value for the primary agent, so they're withheld from the default set and
    // handed only to /assist's diagnose loop instead (see SubagentPlugin's diagnosticTools).
    internal static readonly HashSet<string> CoreSessionTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "repl_session_get_context_status",
    };

    internal static readonly HashSet<string> SessionDiagnosticTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "repl_session_current", "repl_session_list", "repl_session_read_event_log", "repl_session_read_log",
    };

    /// <summary>Every built-in category and optional plugin, in the order the tools menu lists them.</summary>
    internal static readonly IReadOnlyList<ToolCategoryInfo> Categories =
    [
        new("FileSystem", "Read, write and edit files",                                   Optional: false),
        new("Search",     "Find files and search their contents",                         Optional: false),
        new("Shell",      "Run commands in a shell",                                      Optional: false),
        new("Git",        "Status, diffs, history and commits",                           Optional: false),
        new("Todo",       "A to-do list the model keeps for multi-step work",             Optional: false),
        new("Ask",        "Ask you a question partway through a task",                    Optional: false),
        new("Session",    "Check how much of the context budget is used",                 Optional: false),
        new("Skills",     "Load and run installed skills",                                Optional: false),
        new("Subagent",   "Hand searches and tasks to helper agents",                     Optional: false),
        new("Extended",   "More file, shell and Git tools: delete, move, push, background jobs", Optional: true),
        new("Http",       "Make HTTP requests",                                           Optional: true),
        new("Scratchpad", "Named notes kept for the chat",                                Optional: true),
        new("Changes",    "Read the log of changes made in this project",                 Optional: true),
        new("SessionContext", "Read and write a summary handed to the next session",      Optional: true),
        new("Chatroom",   "Message other agents in a shared chatroom",                    Optional: true),
    ];

    /// <summary>Optional plugins: off unless <c>repl.plugins</c> or <c>--plugins</c> names them.</summary>
    internal static readonly HashSet<string> OptionalPlugins =
        new(Categories.Where(c => c.Optional).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    internal const string McpPrefix = "mcp:";

    internal static bool IsMcp(string category) => category.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The menu's line for a category; an MCP server's names the server.</summary>
    internal static string Describe(string category) =>
        Categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))?.Description
        ?? (IsMcp(category) ? $"Tools from the {category[McpPrefix.Length..]} MCP server" : string.Empty);

    /// <summary>
    /// A category's tools without starting a chat, as (name, description); null for the ones only a running chat
    /// knows: Skills (made by the skills provider) and MCP servers (listed by the server once connected).
    /// </summary>
    internal static IReadOnlyList<(string Name, string Description)>? StaticTools(string category)
    {
        var fs    = Tools(typeof(FileSystemPlugin)).Concat(Tools(typeof(FileSystemManagementOps))).ToList();
        var shell = Tools(typeof(ShellPlugin));
        var git   = Tools(typeof(GitPlugin));
        return category.ToLowerInvariant() switch
        {
            "filesystem"     => [.. fs.Where(t => CoreFileSystemTools.Contains(t.Name))],
            "shell"          => [.. shell.Where(t => CoreShellTools.Contains(t.Name))],
            "git"            => [.. git.Where(t => CoreGitTools.Contains(t.Name))],
            "extended"       => [.. fs.Where(t => !CoreFileSystemTools.Contains(t.Name))
                                     .Concat(shell.Where(t => !CoreShellTools.Contains(t.Name)))
                                     .Concat(git.Where(t => !CoreGitTools.Contains(t.Name)))],
            "search"         => Tools(typeof(SearchPlugin)),
            "todo"           => Tools(typeof(TodoPlugin)),
            "ask"            => Tools(typeof(AskPlugin)),
            "session"        => [.. Tools(typeof(ReplSessionPlugin)).Where(t => CoreSessionTools.Contains(t.Name))],
            "subagent"       => Tools(typeof(SubagentPlugin)),
            "http"           => Tools(typeof(HttpPlugin)),
            "scratchpad"     => Tools(typeof(ScratchpadPlugin)),
            "changes"        => Tools(typeof(ChangesPlugin)),
            "sessioncontext" => Tools(typeof(SessionContextPlugin)),
            "chatroom"       => Tools(typeof(ChatroomPlugin)),
            _                => null,
        };
    }

    private static IReadOnlyList<(string Name, string Description)> Tools(Type pluginType) =>
        [.. PluginRegistry.DescribeTools(pluginType).Select(t => (t.Name, FirstSentence(t.Description)))];

    /// <summary>A tool's description cut to its first sentence, for a one-line menu entry.</summary>
    internal static string FirstSentence(string? description)
    {
        var text = (description ?? string.Empty).ReplaceLineEndings(" ").Trim();
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }
}
