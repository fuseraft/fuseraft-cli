using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class ReplApprovalsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-approvals-").FullName;
    private readonly string _workspace;
    private readonly ReplApprovalStore _store;

    public ReplApprovalsTests()
    {
        _workspace = Directory.CreateDirectory(Path.Combine(_dir, "project")).FullName;
        _store = new ReplApprovalStore(Path.Combine(_dir, "approvals.json"), _workspace);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ReplApprovals New() => new(_store, _workspace);

    // Answers every prompt with `answer`, recording the "always" label each one offered.
    private static Func<string?, Task<ApprovalAnswer>> Answer(ApprovalAnswer answer, List<string?> offered) =>
        label =>
        {
            offered.Add(label);
            return Task.FromResult(answer);
        };

    [Fact]
    public async Task Always_for_a_command_covers_its_programs_and_is_saved_for_the_directory()
    {
        var offered = new List<string?>();

        Assert.True(await New().ShellAsync("npm install && npm test", Answer(ApprovalAnswer.Always, offered)));

        Assert.Equal("commands using npm (saved for this directory)", Assert.Single(offered));
        var later = New(); // a later session in the same directory
        Assert.True(await later.ShellAsync("npm run build", Answer(ApprovalAnswer.No, offered)));
        Assert.Single(offered); // not asked again
        Assert.False(await later.ShellAsync("npm test | tee log", Answer(ApprovalAnswer.No, offered))); // tee isn't covered
    }

    [Fact]
    public async Task An_interpreter_is_remembered_for_the_session_only()
    {
        var offered = new List<string?>();
        var approvals = New();

        await approvals.ShellAsync("python3 build.py", Answer(ApprovalAnswer.Always, offered));

        Assert.Equal("commands using python3", offered[0]);
        Assert.True(await approvals.ShellAsync("python3 other.py", Answer(ApprovalAnswer.No, offered)));
        Assert.Empty(_store.Load().Programs);
    }

    [Theory]
    [InlineData("LD_PRELOAD=./x.so ls")]
    [InlineData("./build.sh")]
    [InlineData("echo \"$(rm -rf x)\"")]
    [InlineData("cd src")]
    public void Always_is_not_offered_for_commands_it_cannot_name_safely(string command) =>
        Assert.Null(ReplApprovals.Programs(command));

    [Fact]
    public async Task Yes_is_not_remembered()
    {
        var offered = new List<string?>();
        var approvals = New();

        await approvals.ShellAsync("make", Answer(ApprovalAnswer.Yes, offered));
        await approvals.ShellAsync("make", Answer(ApprovalAnswer.Yes, offered));

        Assert.Equal(2, offered.Count);
    }

    [Fact]
    public async Task Always_for_a_file_write_covers_the_workspace_for_this_session_only()
    {
        var offered = new List<string?>();
        var approvals = New();

        await approvals.FileWriteAsync(Path.Combine(_workspace, "a.cs"), Answer(ApprovalAnswer.Always, offered));
        Assert.True(await approvals.FileWriteAsync(Path.Combine(_workspace, "src", "b.cs"), Answer(ApprovalAnswer.No, offered)));
        Assert.Single(offered);

        Assert.False(await New().FileWriteAsync(Path.Combine(_workspace, "a.cs"), Answer(ApprovalAnswer.No, offered)));
        Assert.Null(await LabelFor(approvals, Path.Combine(_dir, "outside.cs")));
    }

    private static async Task<string?> LabelFor(ReplApprovals approvals, string path)
    {
        string? label = "unset";
        await approvals.FileWriteAsync(path, l => { label = l; return Task.FromResult(ApprovalAnswer.No); });
        return label;
    }

    [Fact]
    public async Task Always_for_a_git_or_http_tool_is_saved_by_tool_name()
    {
        var offered = new List<string?>();

        await New().ToolActionAsync("Git", "git_commit", "fix: x", Answer(ApprovalAnswer.Always, offered));

        Assert.Equal("all git_commit calls (saved for this directory)", offered[0]);
        Assert.Equal(["git_commit"], _store.Load().Tools);
        Assert.True(await New().ToolActionAsync("Git", "git_commit", "another", Answer(ApprovalAnswer.No, offered)));
        Assert.False(await New().ToolActionAsync("Git", "git_push", "origin", Answer(ApprovalAnswer.No, offered)));
    }

    [Fact]
    public async Task A_filesystem_action_is_offered_only_inside_the_workspace_and_not_saved()
    {
        var offered = new List<string?>();
        var approvals = New();
        var inside = Path.Combine(_workspace, "old.txt");

        await approvals.ToolActionAsync("FileSystem", "delete_file", inside, Answer(ApprovalAnswer.Always, offered));
        await approvals.ToolActionAsync("FileSystem", "move_file", $"{inside} → {Path.Combine(_dir, "out.txt")}", Answer(ApprovalAnswer.No, offered));

        Assert.Equal(["all delete_file calls in the workspace, this session", null], offered);
        Assert.True(await approvals.ToolActionAsync("FileSystem", "delete_file", Path.Combine(_workspace, "b.txt"), Answer(ApprovalAnswer.No, offered)));
        Assert.Empty(_store.Load().Tools);
    }

    [Fact]
    public async Task A_sandbox_escape_is_never_offered_always_nor_answered_by_one()
    {
        var offered = new List<string?>();
        var approvals = New();
        await approvals.ToolActionAsync("FileSystem", "read_file", Path.Combine(_workspace, "a"), Answer(ApprovalAnswer.Always, offered));

        var escaped = await approvals.ToolActionAsync("FileSystem", "read_file",
            $"/etc/hosts {IncludedRootsState.EscapeMarker} — grants '/etc' for the rest of this session", Answer(ApprovalAnswer.No, offered));

        Assert.False(escaped);
        Assert.Null(offered[^1]);
    }

    [Fact]
    public async Task Clear_forgets_saved_and_session_answers()
    {
        var offered = new List<string?>();
        var approvals = New();
        await approvals.ShellAsync("npm test", Answer(ApprovalAnswer.Always, offered));
        await approvals.FileWriteAsync(Path.Combine(_workspace, "a"), Answer(ApprovalAnswer.Always, offered));

        approvals.Clear();

        Assert.Empty(_store.Load().Programs);
        Assert.False(await approvals.ShellAsync("npm test", Answer(ApprovalAnswer.No, offered)));
        Assert.False(await approvals.FileWriteAsync(Path.Combine(_workspace, "a"), Answer(ApprovalAnswer.No, offered)));
    }

    [Fact]
    public async Task Saved_answers_belong_to_their_directory()
    {
        var offered = new List<string?>();
        await New().ShellAsync("npm test", Answer(ApprovalAnswer.Always, offered));

        var other = Directory.CreateDirectory(Path.Combine(_dir, "other")).FullName;
        var elsewhere = new ReplApprovals(new ReplApprovalStore(Path.Combine(_dir, "approvals.json"), other), other);

        Assert.False(await elsewhere.ShellAsync("npm test", Answer(ApprovalAnswer.No, offered)));
    }
}
