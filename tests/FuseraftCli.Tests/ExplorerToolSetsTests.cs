using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Pins the exact contents of <see cref="ExplorerToolSets"/> — the single source of truth the
/// REPL's explorer/locate/delegate tools (ReplCommand.cs) and orchestration's SubAgent plugin
/// default fallback (AgentToolResolver.BuildSubAgentTools) both read instead of each hand-copying
/// the same three tool-name sets. A change here is a deliberate, visible edit to what both
/// call sites treat as "safe to hand a read-only delegated agent" — not a silent one-sided drift.
/// </summary>
public sealed class ExplorerToolSetsTests
{
    private static void AssertSetEquals(IEnumerable<string> expected, IReadOnlySet<string> actual)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        Assert.True(expectedSet.SetEquals(actual),
            $"Expected {{{string.Join(", ", expectedSet)}}} but got {{{string.Join(", ", actual)}}}");
    }

    [Fact]
    public void FileSystemRead_ContainsOnlyReadOnlyOperations() =>
        AssertSetEquals(
            ["read_file", "list_files", "grep_file", "get_file_summary", "get_file_info"],
            ExplorerToolSets.FileSystemRead);

    [Fact]
    public void ShellRead_ContainsOnlyRunAndReadOnlyHelpers() =>
        AssertSetEquals(
            ["shell_run", "shell_get_env", "shell_which", "shell_get_working_directory"],
            ExplorerToolSets.ShellRead);

    [Fact]
    public void GitRead_ContainsOnlyReadOnlyOperations() =>
        AssertSetEquals(
            ["git_status", "git_diff", "git_log", "git_show", "git_branch_list", "git_stash_list"],
            ExplorerToolSets.GitRead);

    [Theory]
    [InlineData("write_file")]
    [InlineData("patch_file")]
    [InlineData("delete_file")]
    [InlineData("shell_run_script")]
    [InlineData("shell_kill_job")]
    [InlineData("git_commit")]
    [InlineData("git_push")]
    [InlineData("git_reset")]
    public void ExplorerSets_ExcludeMutatingOrDestructiveTools(string mutatingTool)
    {
        Assert.DoesNotContain(mutatingTool, ExplorerToolSets.FileSystemRead);
        Assert.DoesNotContain(mutatingTool, ExplorerToolSets.ShellRead);
        Assert.DoesNotContain(mutatingTool, ExplorerToolSets.GitRead);
    }
}
