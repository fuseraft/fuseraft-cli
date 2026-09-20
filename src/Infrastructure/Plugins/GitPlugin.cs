using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using fuseraft.Core;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents access to common Git operations.
/// Requires <c>git</c> to be installed and available on PATH.
///
/// When <paramref name="sandboxRoot"/> is provided (recommended for production, and required
/// for parity with <see cref="FileSystemPlugin"/>/<see cref="ShellPlugin"/>), every method's
/// <c>repoPath</c>/<c>directory</c> argument is resolved to its absolute canonical form and
/// rejected if it falls outside the sandbox tree — including read-only queries, since
/// <c>git log</c>/<c>git show</c> against an arbitrary path outside the sandbox is an
/// information-disclosure concern, not just a write-safety one.
///
/// <para>
/// When <c>denyPatterns</c> are supplied (the same FileSystem deny globs the FileSystem plugin
/// enforces), the contents of a protected file never come back through Git either: patch output has
/// the body of a denied file's section replaced by a notice (<see cref="GitDeniedContentFilter"/>),
/// and <c>git_show</c> refuses <c>&lt;ref&gt;:&lt;path&gt;</c> for a denied path and a bare blob hash
/// (which has no path to check).
/// </para>
/// </summary>
public sealed class GitPlugin
{
    private readonly Func<string, string, Task<bool>>? _approveAction;
    private readonly string? _sandboxRoot;
    private readonly IncludedRootsState _includedRoots;
    private readonly Matcher? _denyMatcher;
    private readonly ConcurrentDictionary<string, string> _topLevels = new();

    public GitPlugin(
        Func<string, string, Task<bool>>? approveAction = null, string? sandboxRoot = null,
        IncludedRootsState? includedRoots = null, IReadOnlyList<string>? denyPatterns = null)
    {
        _approveAction = approveAction;
        _sandboxRoot   = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : null;
        _includedRoots = includedRoots ?? IncludedRootsState.Empty;
        _denyMatcher   = FileSystemSandbox.BuildDenyMatcher(denyPatterns);
    }

    // Validates that repoPath (or directory, for InitAsync) stays within the sandbox. When a
    // sandbox is active and no path is specified, defaults to the sandbox root so commands
    // never run against an uncontrolled directory. Applies to every method below, read-only
    // queries included — see the sandboxRoot doc comment above for why.
    // Returns a [DENIED] error string on violation, null when safe.
    private string? ValidateRepoPath(string? repoPath, out string? resolved) =>
        FileSystemSandbox.ResolveSafeDirectory(repoPath, _sandboxRoot, _includedRoots.Snapshot(), out resolved);

    // Read-only queries

    [Description("Get working-tree status.")]
    public async Task<string> StatusAsync([Description("Repo path.")] string? repoPath = null)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var result = await Git("status --short --branch", resolved);
        return result.ToPluginOutput();
    }

    [Description("Show working-tree or staged diff.")]
    public async Task<string> DiffAsync(
        [Description("Repo path.")] string? repoPath = null,
        [Description("Show staged diff.")] bool staged = false,
        [Description("Max output lines.")] int maxLines = 200)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var args = staged ? "diff --cached" : "diff";
        var result = await Git(args, resolved);
        return TruncateLines(result.ToPluginOutput(), maxLines);
    }

    [Description("Show commit history.")]
    public async Task<string> LogAsync(
        [Description("Repo path.")] string? repoPath = null,
        [Description("Max commits.")] int count = 10,
        [Description("Branch or ref.")] string? @ref = null)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var refArg = string.IsNullOrWhiteSpace(@ref) ? string.Empty : $" {@ref}";
        var result = await Git(
            $"log --oneline --decorate -n {count}{refArg}", resolved);
        return result.ToPluginOutput();
    }

    [Description("Show a specific commit.")]
    public async Task<string> ShowAsync(
        [Description("Commit hash or ref.")] string commitRef,
        [Description("Repo path.")] string? repoPath = null,
        [Description("Max output lines.")] int maxLines = 300)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var blobDenial = await CheckShowTargetsAsync(commitRef, resolved);
        if (blobDenial is not null) return blobDenial;

        var result = await Git($"show {commitRef}", resolved);
        return TruncateLines(result.ToPluginOutput(), maxLines);
    }

    [Description("List branches.")]
    public async Task<string> BranchListAsync(
        [Description("Repo path.")] string? repoPath = null,
        [Description("Include remote-tracking branches.")] bool includeRemotes = false)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var args = includeRemotes ? "branch -a" : "branch";
        var result = await Git(args, resolved);
        return result.ToPluginOutput();
    }

    // Write operations

    // Called before every write operation below in --hitl mode. Returns a [DENIED] string
    // when the user blocks the action, or null to proceed — mirrors ShellPlugin's
    // approveCommand gate, generalized across Git's write surface.
    private async Task<string?> CheckApprovalAsync(string action, string detail)
    {
        if (_approveAction is not null && !await _approveAction(action, detail))
            return PluginResult.Denied("Git operation blocked by user.");
        return null;
    }

    [Description("Stage files for commit.")]
    public async Task<string> AddAsync(
        [Description("File path(s) or '.' for everything.")] string paths,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_add", paths);
        if (approvalDenial is not null) return approvalDenial;

        // Split on whitespace so "src/ tests/" stages two paths safely.
        var parts = paths.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = new[] { "add" }.Concat(parts);
        var result = await ProcessHelper.RunAsync("git", args, resolved);
        // Always unstage fuseraft's own working directory — .fuseraft/ contains session
        // artifacts (event logs, summaries, memory) that should never be committed by the agent.
        await ProcessHelper.RunAsync("git", ["reset", "--", ".fuseraft/"], resolved);
        return result.ToPluginOutput();
    }

    [Description("Create a commit.")]
    public async Task<string> CommitAsync(
        [Description("Commit message.")] string message,
        [Description("Repo path.")] string? repoPath = null,
        [Description("Stage all tracked changes before commit.")] bool stageAll = false)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_commit", message);
        if (approvalDenial is not null) return approvalDenial;

        var args = stageAll
            ? new[] { "commit", "-a", "-m", message }
            : new[] { "commit", "-m", message };
        var result = await ProcessHelper.RunAsync("git", args, resolved);
        return result.ToPluginOutput();
    }

    [Description("Switch branch or restore files.")]
    public async Task<string> CheckoutAsync(
        [Description("Branch, commit, or file path.")] string target,
        [Description("Repo path.")] string? repoPath = null,
        [Description("Create branch if it doesn't exist.")] bool createBranch = false)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_checkout", target);
        if (approvalDenial is not null) return approvalDenial;

        var args = createBranch
            ? new[] { "checkout", "-b", target }
            : new[] { "checkout", target };
        var result = await ProcessHelper.RunAsync("git", args, resolved);
        return result.ToPluginOutput();
    }

    [Description("Create a new branch from HEAD.")]
    public async Task<string> CreateBranchAsync(
        [Description("Branch name.")] string branchName,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_create_branch", branchName);
        if (approvalDenial is not null) return approvalDenial;

        var result = await ProcessHelper.RunAsync("git", ["checkout", "-b", branchName], resolved);
        return result.ToPluginOutput();
    }

    [Description("Initialize a git repository.")]
    public async Task<string> InitAsync([Description("Directory path.")] string? directory = null)
    {
        var pathDenial = ValidateRepoPath(directory, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_init", resolved ?? "(cwd)");
        if (approvalDenial is not null) return approvalDenial;

        var result = await Git("init", resolved);
        return result.ToPluginOutput();
    }

    [Description("Returns 'true' if the path is inside a git working tree, 'false' otherwise. " +
                 "Note: this is also 'true' for a plain subdirectory of some ancestor repo that " +
                 "has no .git of its own — use is_repo_root instead when the question is whether " +
                 "it is safe to commit here as this project's own history.")]
    public async Task<string> IsInsideWorkTreeAsync(
        [Description("Repo path to check (defaults to CWD).")] string? repoPath = null)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var result = await Git("rev-parse --is-inside-work-tree", resolved);
        return result.ExitCode is 128 or 129 ? "false"
             : result.Succeeded               ? "true"
             : "false";
    }

    [Description("Returns 'true' if this exact path is itself the root of a git working tree " +
                 "(has its own .git), 'false' if it is not a repo at all or is merely nested " +
                 "inside an ancestor repo's working tree. Prefer this over is_inside_work_tree " +
                 "before committing: a project directory can be 'inside a work tree' purely by " +
                 "being nested under some unrelated ancestor repo (e.g. a scratch folder under a " +
                 "dotfiles-tracked home directory) — committing there would land in that ancestor's " +
                 "history and be subject to its .gitignore, not this project's own.")]
    public async Task<string> IsRepoRootAsync(
        [Description("Directory to check (defaults to CWD).")] string? repoPath = null)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var result = await Git("rev-parse --show-toplevel", resolved);
        if (!result.Succeeded) return "false";

        var toplevel = result.Stdout.Trim().TrimEnd('/', '\\');
        var target   = Path.GetFullPath(string.IsNullOrWhiteSpace(resolved)
            ? Directory.GetCurrentDirectory()
            : ProcessHelper.ExpandHome(resolved)).TrimEnd('/', '\\');

        return string.Equals(toplevel, target, StringComparison.Ordinal) ? "true" : "false";
    }

    [Description("Push commits to a remote.")]
    public async Task<string> PushAsync(
        [Description("Remote name.")] string? remote = null,
        [Description("Branch to push.")] string? branch = null,
        [Description("Set upstream tracking reference.")] bool setUpstream = false,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_push", $"{remote ?? "(default remote)"} {branch ?? ""}".Trim());
        if (approvalDenial is not null) return approvalDenial;

        var args = new List<string> { "push" };
        if (setUpstream) args.Add("--set-upstream");
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote);
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch);
        var result = await ProcessHelper.RunAsync("git", args, resolved, timeoutSeconds: 120);
        return result.ToPluginOutput();
    }

    [Description("Pull changes from a remote.")]
    public async Task<string> PullAsync(
        [Description("Remote name.")] string? remote = null,
        [Description("Branch to pull.")] string? branch = null,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_pull", $"{remote ?? "(default remote)"} {branch ?? ""}".Trim());
        if (approvalDenial is not null) return approvalDenial;

        var args = new List<string> { "pull" };
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote);
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch);
        var result = await ProcessHelper.RunAsync("git", args, resolved, timeoutSeconds: 120);
        return result.ToPluginOutput();
    }

    [Description("Stash working-tree changes.")]
    public async Task<string> StashAsync(
        [Description("Stash message.")] string? message = null,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_stash", message ?? "(no message)");
        if (approvalDenial is not null) return approvalDenial;

        var args = string.IsNullOrWhiteSpace(message) ? "stash push" : $"stash push -m \"{message}\"";
        var result = await Git(args, resolved);
        return result.ToPluginOutput();
    }

    [Description("List stashed changesets.")]
    public async Task<string> StashListAsync(
        [Description("Repo path.")] string? repoPath = null)
    {
        var denial = ValidateRepoPath(repoPath, out var resolved);
        if (denial is not null) return denial;

        var result = await Git("stash list", resolved);
        return result.ToPluginOutput();
    }

    [Description("Apply and remove the most recent stash.")]
    public async Task<string> StashPopAsync(
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_stash_pop", resolved ?? "(cwd)");
        if (approvalDenial is not null) return approvalDenial;

        var result = await Git("stash pop", resolved);
        return result.ToPluginOutput();
    }

    [Description("Reset HEAD. soft: moves HEAD; mixed: unstages; hard: discards all changes.")]
    public async Task<string> ResetAsync(
        [Description("Reset mode: 'soft', 'mixed', or 'hard'.")] string mode = "mixed",
        [Description("Target ref.")] string @ref = "HEAD",
        [Description("Repo path.")] string? repoPath = null)
    {
        mode = mode.ToLowerInvariant();
        if (mode is not ("soft" or "mixed" or "hard"))
            return PluginResult.Error($"Invalid mode '{mode}'. Must be 'soft', 'mixed', or 'hard'.");

        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var approvalDenial = await CheckApprovalAsync("git_reset", $"--{mode} {@ref}");
        if (approvalDenial is not null) return approvalDenial;

        var result = await Git($"reset --{mode} {@ref}", resolved);
        return result.ToPluginOutput();
    }

    [Description("Rebase the current branch onto an upstream ref, or control an in-progress rebase. " +
                 "For a simple rebase supply upstream. For --onto supply both onto and upstream. " +
                 "To abort, continue, or skip a rebase in progress, supply control only.")]
    public async Task<string> RebaseAsync(
        [Description("Upstream ref (branch, commit, or HEAD~N). Required unless using control.")] string? upstream = null,
        [Description("New base for --onto rebase. Requires upstream.")] string? onto = null,
        [Description("Control an in-progress rebase: 'abort', 'continue', or 'skip'.")] string? control = null,
        [Description("Repo path.")] string? repoPath = null)
    {
        var pathDenial = ValidateRepoPath(repoPath, out var resolved);
        if (pathDenial is not null) return pathDenial;

        if (!string.IsNullOrWhiteSpace(control))
        {
            control = control.Trim().ToLowerInvariant();
            if (control is not ("abort" or "continue" or "skip"))
                return PluginResult.Error($"Invalid control value '{control}'. Must be 'abort', 'continue', or 'skip'.");

            var controlApprovalDenial = await CheckApprovalAsync("git_rebase", $"--{control}");
            if (controlApprovalDenial is not null) return controlApprovalDenial;

            var result = await ProcessHelper.RunAsync("git", ["rebase", $"--{control}"], resolved);
            return result.ToPluginOutput();
        }

        if (string.IsNullOrWhiteSpace(upstream))
            return PluginResult.Error("upstream is required when not using control.");

        var approvalDenial = await CheckApprovalAsync("git_rebase", onto is { Length: > 0 } ? $"--onto {onto} {upstream}" : upstream);
        if (approvalDenial is not null) return approvalDenial;

        if (!string.IsNullOrWhiteSpace(onto))
        {
            var result = await ProcessHelper.RunAsync("git", ["rebase", "--onto", onto.Trim(), upstream.Trim()], resolved);
            return result.ToPluginOutput();
        }
        else
        {
            var result = await ProcessHelper.RunAsync("git", ["rebase", upstream.Trim()], resolved);
            return result.ToPluginOutput();
        }
    }

    // Helpers

    // Every Git tool that shells out through here gets patch output scrubbed of denied files' contents,
    // whichever tool produced it (git_diff, git_show, or git_log given `-p` as its ref).
    private async Task<ProcessResult> Git(string args, string? workingDirectory = null)
    {
        var result = await ProcessHelper.RunAsync("git", args, workingDirectory);
        if (_denyMatcher is null || !GitDeniedContentFilter.ContainsPatch(result.Stdout)) return result;

        var top = await TopLevelAsync(workingDirectory);
        return result with
        {
            Stdout = GitDeniedContentFilter.HideDeniedSections(result.Stdout, top, _denyMatcher, _sandboxRoot, out _),
        };
    }

    // Paths in a diff header, and in `<ref>:<path>`, are relative to the repository top-level.
    private async Task<string> TopLevelAsync(string? workingDirectory)
    {
        var key = workingDirectory ?? Directory.GetCurrentDirectory();
        if (_topLevels.TryGetValue(key, out var cached)) return cached;

        var result = await ProcessHelper.RunAsync("git", "rev-parse --show-toplevel", workingDirectory);
        var top = result.Succeeded && !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout.Trim() : key;
        return _topLevels[key] = top;
    }

    // `git show HEAD:.env` prints a raw blob — no `diff --git` header for the filter to see — so the
    // path in a `<ref>:<path>` argument is checked up front. A bare blob hash (`git show 3f2a…`) has no
    // path at all, so it can't be checked and is refused; `git show HEAD:<path>` is the way to show one.
    // Arguments after `--` are pathspecs (they narrow a diff, whose output is filtered), and options
    // are skipped.
    private async Task<string?> CheckShowTargetsAsync(string commitRef, string? workingDirectory)
    {
        if (_denyMatcher is null) return null;

        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        string? top = null;

        foreach (var token in commitRef.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "--") break;
            if (token.StartsWith('-')) continue;

            if (TryGetBlobPath(token, out var path))
            {
                top ??= await TopLevelAsync(workingDirectory);
                foreach (var baseDir in new[] { top, cwd })   // `HEAD:./x` is relative to the cwd, `HEAD:x` to the top-level
                {
                    string full;
                    try { full = Path.GetFullPath(Path.Combine(baseDir, path.Replace('\\', '/'))); }
                    catch (ArgumentException) { continue; }

                    if (FileSystemSandbox.MatchesDenyRule(_denyMatcher, full, _sandboxRoot))
                        return FileSystemSandbox.DenyRuleDenial(full);
                }
                continue;
            }

            var type = await ProcessHelper.RunAsync("git", ["cat-file", "-t", token], workingDirectory);
            if (type.Succeeded && type.Stdout.Trim() == "blob")
                return PluginResult.Denied(
                    "git show of a bare blob object can't be checked against FileSystem deny rules " +
                    "(a blob hash has no path). Show it by path instead, e.g. `git show HEAD:<path>`.");
        }
        return null;
    }

    // `HEAD:path`, `v1.0:dir/file`, `:path` and `:0:path` (index stages) name a blob by path.
    private static bool TryGetBlobPath(string token, out string path)
    {
        path = string.Empty;
        var colon = token.IndexOf(':');
        if (colon < 0) return false;

        var rest = token[(colon + 1)..];
        if (colon == 0 && Regex.Match(rest, @"^\d:(.*)$") is { Success: true } stage)
            rest = stage.Groups[1].Value;

        path = rest;
        return path.Length > 0;
    }

    private static string TruncateLines(string text, int maxLines)
    {
        if (maxLines <= 0) return text;
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        return string.Join('\n', lines[..maxLines]) + $"\n... [{lines.Length - maxLines} lines truncated]";
    }
}
