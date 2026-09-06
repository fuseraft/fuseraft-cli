namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Maps built-in tool function names to their owning plugin and required capability tag.
///
/// <para>
/// When an agent declares <c>Capabilities</c> for a plugin, <see cref="IsAllowed"/>
/// is called for each tool in that plugin's function list. Only tools whose capability
/// tag appears in the declared list are registered for that agent. <see cref="GetPlugin"/>
/// is the reverse lookup — given a tool name, which plugin owns it — used by the REPL's
/// <c>/tools restrict</c> command to apply the same per-plugin capability filter to whichever
/// REPL tool category currently holds that tool (Core or Extended), since a tool's owning
/// plugin is a property of the tool itself, not of which REPL bucket it happens to be in.
/// </para>
///
/// <para>
/// Tools whose names are absent from this map are always allowed. This is the
/// forward-compatible default: MCP-registered tools and future built-ins are never
/// silently blocked by a capability filter that has not been updated yet.
/// </para>
///
/// <para>
/// Capability vocabulary by plugin:
/// <list type="table">
///   <item><term>FileSystem</term><description><c>read</c> (read_file, grep_file, get_file_summary, get_file_info, list_files) · <c>write</c> (write_file, patch_file, save_file_summary, create_directory, copy_file, move_file, set_permissions) · <c>delete</c> (delete_file, delete_directory)</description></item>
///   <item><term>Shell</term><description><c>read</c> (get_env, get_job_status, get_job_output, which, working_directory) · <c>run</c> (shell_run, shell_run_script, shell_run_background, shell_set_env, shell_kill_job)</description></item>
///   <item><term>Git</term><description><c>read</c> (status/diff/log/show/branch_list/stash_list/is_inside_work_tree/is_repo_root) · <c>write</c> (add/commit/checkout/create_branch/init/push/pull/stash/stash_pop/reset/rebase)</description></item>
///   <item><term>Http</term><description><c>get</c> · <c>post</c> · <c>put</c> · <c>patch</c> · <c>delete</c> — one per HTTP verb</description></item>
///   <item><term>Json</term><description><c>read</c> (format, minify, get, keys, search, to_text, validate) · <c>write</c> (merge)</description></item>
///   <item><term>Document</term><description><c>read</c> (extract_text, get_info, list_sheets, get_sheet — all read-only)</description></item>
///   <item><term>Search</term><description><c>read</c> (all search operations are read-only)</description></item>
///   <item><term>Changes</term><description><c>read</c> (read, read_latest)</description></item>
///   <item><term>Scratchpad</term><description><c>read</c> (read, read_all, search) · <c>write</c> (write, delete)</description></item>
///   <item><term>Chatroom</term><description><c>read</c> · <c>write</c> (send)</description></item>
///   <item><term>Probe</term><description><c>run</c> (all probe operations execute code)</description></item>
///   <item><term>CodeExecution</term><description><c>read</c> (check_docker) · <c>execute</c> (sandbox_run, repl_*)</description></item>
///   <item><term>Decision</term><description><c>read</c> (search, read) · <c>write</c> (create, supersede)</description></item>
///   <item><term>Graph</term><description><c>read</c> (search, refs, dependents — all read-only)</description></item>
/// </list>
/// </para>
/// </summary>
internal static class PluginCapabilityMap
{
    private static readonly Dictionary<string, (string Plugin, string Capability)> ToolInfo =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // FileSystem (NoPrefixPlugin — no class prefix in tool name)
        ["read_file"]                      = ("FileSystem", "read"),
        ["grep_file"]                      = ("FileSystem", "read"),
        ["get_file_summary"]               = ("FileSystem", "read"),
        ["get_file_info"]                  = ("FileSystem", "read"),
        ["list_files"]                     = ("FileSystem", "read"),
        ["set_permissions"]                = ("FileSystem", "write"),
        ["write_file"]                     = ("FileSystem", "write"),
        ["patch_file"]                     = ("FileSystem", "write"),
        ["save_file_summary"]              = ("FileSystem", "write"),
        ["create_directory"]               = ("FileSystem", "write"),
        ["copy_file"]                      = ("FileSystem", "write"),
        ["move_file"]                      = ("FileSystem", "write"),
        ["delete_file"]                    = ("FileSystem", "delete"),
        ["delete_directory"]               = ("FileSystem", "delete"),

        // Shell
        ["shell_run"]                      = ("Shell", "run"),
        ["shell_run_script"]               = ("Shell", "run"),
        ["shell_run_background"]           = ("Shell", "run"),
        ["shell_set_env"]                  = ("Shell", "run"),
        ["shell_get_env"]                  = ("Shell", "read"),
        ["shell_get_job_status"]           = ("Shell", "read"),
        ["shell_get_job_output"]           = ("Shell", "read"),
        ["shell_kill_job"]                 = ("Shell", "run"),
        ["shell_which"]                    = ("Shell", "read"),
        ["shell_get_working_directory"]    = ("Shell", "read"),
        ["shell_get_session_temp_dir"]     = ("Shell", "read"),

        // Git
        ["git_status"]                     = ("Git", "read"),
        ["git_diff"]                       = ("Git", "read"),
        ["git_log"]                        = ("Git", "read"),
        ["git_show"]                       = ("Git", "read"),
        ["git_branch_list"]                = ("Git", "read"),
        ["git_stash_list"]                 = ("Git", "read"),
        ["git_is_inside_work_tree"]        = ("Git", "read"),
        ["git_is_repo_root"]               = ("Git", "read"),
        ["git_add"]                        = ("Git", "write"),
        ["git_commit"]                     = ("Git", "write"),
        ["git_checkout"]                   = ("Git", "write"),
        ["git_create_branch"]              = ("Git", "write"),
        ["git_init"]                       = ("Git", "write"),
        ["git_push"]                       = ("Git", "write"),
        ["git_pull"]                       = ("Git", "write"),
        ["git_stash"]                      = ("Git", "write"),
        ["git_stash_pop"]                  = ("Git", "write"),
        ["git_reset"]                      = ("Git", "write"),
        ["git_rebase"]                     = ("Git", "write"),

        // Http (one capability per HTTP verb for fine-grained control)
        ["http_get"]                       = ("Http", "get"),
        ["http_head"]                      = ("Http", "get"),
        ["http_post"]                      = ("Http", "post"),
        ["http_put"]                       = ("Http", "put"),
        ["http_patch"]                     = ("Http", "patch"),
        ["http_delete"]                    = ("Http", "delete"),

        // Json
        ["json_format"]                    = ("Json", "read"),
        ["json_minify"]                    = ("Json", "read"),
        ["json_get"]                       = ("Json", "read"),
        ["json_keys"]                      = ("Json", "read"),
        ["json_search"]                    = ("Json", "read"),
        ["json_to_text"]                   = ("Json", "read"),
        ["json_validate"]                  = ("Json", "read"),
        ["json_merge"]                     = ("Json", "write"),

        // Document (all read-only)
        ["document_extract_text"]          = ("Document", "read"),
        ["document_get_info"]              = ("Document", "read"),
        ["document_list_sheets"]           = ("Document", "read"),
        ["document_get_sheet"]             = ("Document", "read"),

        // Search (all read-only)
        ["search_content"]                 = ("Search", "read"),
        ["search_symbol"]                  = ("Search", "read"),
        ["search_callers"]                 = ("Search", "read"),

        // Changes (read-only consumer of the change log)
        ["changes_read"]                   = ("Changes", "read"),
        ["changes_read_latest"]            = ("Changes", "read"),

        // Scratchpad
        ["scratchpad_read"]                = ("Scratchpad", "read"),
        ["scratchpad_read_all"]            = ("Scratchpad", "read"),
        ["scratchpad_search"]              = ("Scratchpad", "read"),
        ["scratchpad_write"]               = ("Scratchpad", "write"),
        ["scratchpad_delete"]              = ("Scratchpad", "write"),

        // Chatroom
        ["chatroom_read"]                  = ("Chatroom", "read"),
        ["chatroom_send"]                  = ("Chatroom", "write"),

        // Probe (all operations execute code)
        ["probe_code"]                     = ("Probe", "run"),
        ["probe_assert_output"]            = ("Probe", "run"),
        ["probe_compare_outputs"]          = ("Probe", "run"),
        ["probe_run_hypothesis"]           = ("Probe", "run"),

        // Decision (ADR Registry)
        ["decision_search"]                = ("Decision", "read"),
        ["decision_read"]                  = ("Decision", "read"),
        ["decision_create"]                = ("Decision", "write"),
        ["decision_supersede"]             = ("Decision", "write"),

        // Graph (repository semantic graph — all tools are read-only)
        ["graph_search"]                   = ("Graph", "read"),
        ["graph_refs"]                     = ("Graph", "read"),
        ["graph_dependents"]               = ("Graph", "read"),

        // CodeExecution
        ["code_execution_check_docker"]    = ("CodeExecution", "read"),
        ["code_execution_sandbox_run"]     = ("CodeExecution", "execute"),
        ["code_execution_repl_start"]      = ("CodeExecution", "execute"),
        ["code_execution_repl_exec"]       = ("CodeExecution", "execute"),
        ["code_execution_repl_reset"]      = ("CodeExecution", "execute"),
        ["code_execution_repl_stop"]       = ("CodeExecution", "execute"),
    };

    /// <summary>
    /// Every plugin name that appears in <see cref="ToolInfo"/> — the set of plugins that
    /// actually have fine-grained capability tags. Used to warn when <c>/tools restrict</c>
    /// is given a plugin name (e.g. a typo, or a plugin like <c>Todo</c> or <c>SubAgent</c>
    /// with no capability entries at all) that could never match a tool.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownPlugins =
        new HashSet<string>(ToolInfo.Values.Select(v => v.Plugin), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> is permitted by
    /// <paramref name="allowedCapabilities"/>.
    ///
    /// <para>
    /// Tools not present in <see cref="ToolInfo"/> are always allowed so that
    /// MCP-registered tools and future built-ins are never silently blocked.
    /// </para>
    /// </summary>
    public static bool IsAllowed(string toolName, IReadOnlyList<string> allowedCapabilities)
    {
        if (!ToolInfo.TryGetValue(toolName, out var info))
            return true; // Unknown tool — pass through unfiltered.

        return allowedCapabilities.Any(c => c.Equals(info.Capability, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the plugin name that owns <paramref name="toolName"/> (e.g. <c>"Git"</c> for
    /// <c>git_commit</c>), or <see langword="null"/> when the tool has no capability entry —
    /// mirrors <see cref="IsAllowed"/>'s pass-through default for MCP tools and future built-ins.
    /// </summary>
    public static string? GetPlugin(string toolName) =>
        ToolInfo.TryGetValue(toolName, out var info) ? info.Plugin : null;

    /// <summary>
    /// Test-only accessor: <see langword="true"/> when <paramref name="toolName"/> has an
    /// explicit capability entry. Used by a coverage test asserting every built-in plugin
    /// tool is mapped, so a newly added tool can't silently bypass capability filtering by
    /// being absent from <see cref="ToolInfo"/> (unmapped tools are always-allowed
    /// by <see cref="IsAllowed"/>, which is the correct default for MCP tools but a silent
    /// gap for a forgotten built-in one).
    /// </summary>
    internal static bool HasCapabilityEntry(string toolName) => ToolInfo.ContainsKey(toolName);
}
