namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Maps built-in tool function names to their required capability tag.
///
/// <para>
/// When an agent declares <c>Capabilities</c> for a plugin, <see cref="IsAllowed"/>
/// is called for each tool in that plugin's function list. Only tools whose capability
/// tag appears in the declared list are registered for that agent.
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
///   <item><term>Git</term><description><c>read</c> (status, diff, log, show, branch_list, stash_list) · <c>write</c> (add, commit, checkout, create_branch, init, push, pull, stash, stash_pop, reset)</description></item>
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
    private static readonly Dictionary<string, string> ToolCapabilities =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // FileSystem (NoPrefixPlugin — no class prefix in tool name)
        ["read_file"]                      = "read",
        ["grep_file"]                      = "read",
        ["get_file_summary"]               = "read",
        ["get_file_info"]                  = "read",
        ["list_files"]                     = "read",
        ["set_permissions"]                = "write",
        ["write_file"]                     = "write",
        ["patch_file"]                     = "write",
        ["save_file_summary"]              = "write",
        ["create_directory"]               = "write",
        ["copy_file"]                      = "write",
        ["move_file"]                      = "write",
        ["delete_file"]                    = "delete",
        ["delete_directory"]               = "delete",

        // Shell
        ["shell_run"]                      = "run",
        ["shell_run_script"]               = "run",
        ["shell_run_background"]           = "run",
        ["shell_set_env"]                  = "run",
        ["shell_get_env"]                  = "read",
        ["shell_get_job_status"]           = "read",
        ["shell_get_job_output"]           = "read",
        ["shell_kill_job"]                 = "run",
        ["shell_which"]                    = "read",
        ["shell_get_working_directory"]    = "read",
        ["shell_get_session_temp_dir"]     = "read",

        // Git
        ["git_status"]                     = "read",
        ["git_diff"]                       = "read",
        ["git_log"]                        = "read",
        ["git_show"]                       = "read",
        ["git_branch_list"]                = "read",
        ["git_stash_list"]                 = "read",
        ["git_is_inside_work_tree"]        = "read",
        ["git_add"]                        = "write",
        ["git_commit"]                     = "write",
        ["git_checkout"]                   = "write",
        ["git_create_branch"]              = "write",
        ["git_init"]                       = "write",
        ["git_push"]                       = "write",
        ["git_pull"]                       = "write",
        ["git_stash"]                      = "write",
        ["git_stash_pop"]                  = "write",
        ["git_reset"]                      = "write",
        ["git_rebase"]                     = "write",

        // Http (one capability per HTTP verb for fine-grained control)
        ["http_get"]                       = "get",
        ["http_head"]                      = "get",
        ["http_post"]                      = "post",
        ["http_put"]                       = "put",
        ["http_patch"]                     = "patch",
        ["http_delete"]                    = "delete",

        // Json
        ["json_format"]                    = "read",
        ["json_minify"]                    = "read",
        ["json_get"]                       = "read",
        ["json_keys"]                      = "read",
        ["json_search"]                    = "read",
        ["json_to_text"]                   = "read",
        ["json_validate"]                  = "read",
        ["json_merge"]                     = "write",

        // Document (all read-only)
        ["document_extract_text"]          = "read",
        ["document_get_info"]              = "read",
        ["document_list_sheets"]           = "read",
        ["document_get_sheet"]             = "read",

        // Search (all read-only)
        ["search_content"]                 = "read",
        ["search_symbol"]                  = "read",
        ["search_callers"]                 = "read",

        // Changes (read-only consumer of the change log)
        ["changes_read"]                   = "read",
        ["changes_read_latest"]            = "read",

        // Scratchpad
        ["scratchpad_read"]                = "read",
        ["scratchpad_read_all"]            = "read",
        ["scratchpad_search"]              = "read",
        ["scratchpad_write"]               = "write",
        ["scratchpad_delete"]              = "write",

        // Chatroom
        ["chatroom_read"]                  = "read",
        ["chatroom_send"]                  = "write",

        // Probe (all operations execute code)
        ["probe_code"]                     = "run",
        ["probe_assert_output"]            = "run",
        ["probe_compare_outputs"]          = "run",
        ["probe_run_hypothesis"]           = "run",

        // Decision (ADR Registry)
        ["decision_search"]                = "read",
        ["decision_read"]                  = "read",
        ["decision_create"]                = "write",
        ["decision_supersede"]             = "write",

        // Graph (repository semantic graph — all tools are read-only)
        ["graph_search"]                   = "read",
        ["graph_refs"]                     = "read",
        ["graph_dependents"]               = "read",

        // CodeExecution
        ["code_execution_check_docker"]    = "read",
        ["code_execution_sandbox_run"]     = "execute",
        ["code_execution_repl_start"]      = "execute",
        ["code_execution_repl_exec"]       = "execute",
        ["code_execution_repl_reset"]      = "execute",
        ["code_execution_repl_stop"]       = "execute",
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> is permitted by
    /// <paramref name="allowedCapabilities"/>.
    ///
    /// <para>
    /// Tools not present in <see cref="ToolCapabilities"/> are always allowed so that
    /// MCP-registered tools and future built-ins are never silently blocked.
    /// </para>
    /// </summary>
    public static bool IsAllowed(string toolName, IReadOnlyList<string> allowedCapabilities)
    {
        if (!ToolCapabilities.TryGetValue(toolName, out var required))
            return true; // Unknown tool — pass through unfiltered.

        return allowedCapabilities.Any(c => c.Equals(required, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Test-only accessor: <see langword="true"/> when <paramref name="toolName"/> has an
    /// explicit capability entry. Used by a coverage test asserting every built-in plugin
    /// tool is mapped, so a newly added tool can't silently bypass capability filtering by
    /// being absent from <see cref="ToolCapabilities"/> (unmapped tools are always-allowed
    /// by <see cref="IsAllowed"/>, which is the correct default for MCP tools but a silent
    /// gap for a forgotten built-in one).
    /// </summary>
    internal static bool HasCapabilityEntry(string toolName) => ToolCapabilities.ContainsKey(toolName);
}
