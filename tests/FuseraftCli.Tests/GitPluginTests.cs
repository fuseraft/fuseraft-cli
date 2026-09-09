using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="GitPlugin"/>'s approveAction gate — the HITL hook `/hitl on`
/// (REPL) and `fuseraft run --hitl` (orchestration) wire into every write operation,
/// generalizing ShellPlugin's approveCommand gate (see ShellPluginTests) to Git — and its
/// sandboxRoot enforcement, which applies to every method (read-only queries included),
/// mirroring FileSystemPlugin's/ShellPlugin's sandbox model.
/// </summary>
public sealed class GitPluginTests : IDisposable
{
    private readonly string _dir;
    private readonly string _outsideDir;

    public GitPluginTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fuseraft_gitplugin_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _outsideDir = Path.Combine(Path.GetTempPath(), "fuseraft_gitplugin_tests_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        if (Directory.Exists(_outsideDir)) Directory.Delete(_outsideDir, recursive: true);
    }

    [Fact]
    public async Task InitAsync_ApproveActionReturnsFalse_BlocksAndDoesNotInit()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.InitAsync(_dir);

        Assert.Contains("[DENIED]", result);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task InitAsync_ApproveActionReturnsTrue_InitsNormally()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(true));

        var result = await plugin.InitAsync(_dir);

        Assert.DoesNotContain("[DENIED]", result);
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task InitAsync_NoApproveAction_ExecutesWithoutBlocking()
    {
        // Default construction (no approver) — the REPL's pre-/hitl behavior, and still the
        // behavior once /hitl is off — must keep working unprompted.
        var plugin = new GitPlugin();

        var result = await plugin.InitAsync(_dir);

        Assert.DoesNotContain("[DENIED]", result);
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task AddAsync_ApproveActionReturnsFalse_BlocksBeforeRunningGit()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.AddAsync(".", _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task CommitAsync_ApproveActionSeesActionNameAndMessage()
    {
        (string Action, string Detail)? seen = null;
        var plugin = new GitPlugin(approveAction: (action, detail) =>
        {
            seen = (action, detail);
            return Task.FromResult(false); // deny — no repo needed, just verifying what's seen
        });

        await plugin.CommitAsync("fix: something", _dir);

        Assert.Equal("git_commit", seen?.Action);
        Assert.Equal("fix: something", seen?.Detail);
    }

    [Fact]
    public async Task CheckoutAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.CheckoutAsync("main", _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task CreateBranchAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.CreateBranchAsync("feature/x", _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task PushAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.PushAsync("origin", "main", repoPath: _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task PullAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.PullAsync("origin", "main", repoPath: _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task StashAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.StashAsync("wip", _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task StashPopAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.StashPopAsync(_dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task ResetAsync_ApproveActionReturnsFalse_Blocks()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.ResetAsync("hard", "HEAD", _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task RebaseAsync_ApproveActionReturnsFalse_Blocks_ForPlainUpstream()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.RebaseAsync(upstream: "main", repoPath: _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task RebaseAsync_ApproveActionReturnsFalse_Blocks_ForControlValue()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.RebaseAsync(control: "abort", repoPath: _dir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task StatusAsync_NotGated_ReadOpsBypassApproval()
    {
        var plugin = new GitPlugin(approveAction: (_, _) => Task.FromResult(false));

        var result = await plugin.StatusAsync(_dir);

        Assert.DoesNotContain("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // sandboxRoot — applies to every method, including read-only queries, since a git
    // command against an arbitrary path outside the sandbox is an information-disclosure
    // concern, not just a write-safety one.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StatusAsync_RepoPathOutsideSandbox_ReturnsDenial()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.StatusAsync(_outsideDir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task LogAsync_RepoPathOutsideSandbox_ReturnsDenial()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.LogAsync(_outsideDir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task StatusAsync_NoRepoPath_DefaultsToSandboxRootRatherThanDenying()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.StatusAsync();

        // No repo at _dir yet, so git itself reports "not a git repository" — the point of
        // this test is that the sandbox check does NOT deny an unspecified repoPath outright.
        Assert.DoesNotContain("[DENIED]", result);
    }

    [Fact]
    public async Task InitAsync_DirectoryOutsideSandbox_ReturnsDenialAndDoesNotInit()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.InitAsync(_outsideDir);

        Assert.Contains("[DENIED]", result);
        Assert.False(Directory.Exists(Path.Combine(_outsideDir, ".git")));
    }

    [Fact]
    public async Task InitAsync_DirectoryInsideSandbox_InitsNormally()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.InitAsync(_dir);

        Assert.DoesNotContain("[DENIED]", result);
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task CommitAsync_RepoPathOutsideSandbox_DeniesBeforeApprovalPrompt()
    {
        var approvalWasAsked = false;
        var plugin = new GitPlugin(
            approveAction: (_, _) => { approvalWasAsked = true; return Task.FromResult(true); },
            sandboxRoot: _dir);

        var result = await plugin.CommitAsync("fix: something", _outsideDir);

        Assert.Contains("[DENIED]", result);
        Assert.False(approvalWasAsked, "the sandbox check should short-circuit before ever prompting for approval");
    }

    [Fact]
    public async Task PushAsync_RepoPathOutsideSandbox_ReturnsDenial()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.PushAsync("origin", "main", repoPath: _outsideDir);

        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task IsRepoRootAsync_RepoPathOutsideSandbox_ReturnsDenialNotFalse()
    {
        var plugin = new GitPlugin(sandboxRoot: _dir);

        var result = await plugin.IsRepoRootAsync(_outsideDir);

        // Distinct from the tool's normal "true"/"false" contract — a sandbox violation is
        // reported the same [DENIED] way every other plugin reports one, consistently.
        Assert.Contains("[DENIED]", result);
    }

    [Fact]
    public async Task AddAsync_NoSandbox_RepoPathPassedThroughUnchanged()
    {
        // Default construction (no sandboxRoot) must keep working exactly as before this
        // change — an arbitrary repoPath is passed straight through, matching the REPL's
        // (deliberately unsandboxed) GitPlugin construction.
        var plugin = new GitPlugin();

        var result = await plugin.StatusAsync(_outsideDir);

        Assert.DoesNotContain("[DENIED]", result);
    }
}
