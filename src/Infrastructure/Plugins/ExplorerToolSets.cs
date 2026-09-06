namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// The default read-only "explorer" tool subset — FileSystem reads, Shell run/read helpers, and
/// Git read-only operations — used wherever a delegated agent needs investigation tools without
/// any mutation capability.
///
/// <para>
/// Single source of truth for two independent call sites that each assemble a read-only
/// delegated agent: the REPL's <c>SubAgentPlugin</c> explorer/locate/delegate tools
/// (<c>ReplCommand.cs</c>) and orchestration's <c>SubAgent</c> plugin default fallback
/// (<c>AgentToolResolver.BuildSubAgentTools</c>). Both previously hand-copied the same three
/// tool-name sets with no reference between them — a tool added to one read-only set silently
/// would not appear in the other.
/// </para>
/// </summary>
internal static class ExplorerToolSets
{
    public static readonly IReadOnlySet<string> FileSystemRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "read_file", "list_files", "grep_file", "get_file_summary", "get_file_info" };

    public static readonly IReadOnlySet<string> ShellRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "shell_run", "shell_get_env", "shell_which", "shell_get_working_directory" };

    public static readonly IReadOnlySet<string> GitRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "git_status", "git_diff", "git_log", "git_show", "git_branch_list", "git_stash_list" };
}
