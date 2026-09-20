using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <c>git_diff</c> and <c>git_show</c> hide the contents of a file a FileSystem deny rule protects, but
/// <c>shell_run "git diff"</c> printed the tracked <c>.env</c> whole: the command text names no
/// protected file, so the shell policy had nothing to object to (verified live in the REPL). The same
/// patch filter now runs over shell output. Real repositories, real <c>git</c>.
/// </summary>
public sealed class ShellPluginDenyPatchTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("fuseraft_shelldeny_").FullName;

    private const string V1 = "SECRET-V1-marker";
    private const string V2 = "SECRET-V2-marker";

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_repo, recursive: true);
    }

    private static async Task GitAsync(string dir, params string[] args)
    {
        var r = await ProcessHelper.RunAsync("git", args, dir);
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)} failed: {r.Stderr}");
    }

    private void Put(string relative, string content)
    {
        var full = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // Plain marker lines, not `KEY=value`, so the only thing that can hide them is the patch filter.
    private async Task InitRepoWithTrackedSecretAsync()
    {
        await GitAsync(_repo, "init", "-q");
        await GitAsync(_repo, "config", "user.email", "t@example.com");
        await GitAsync(_repo, "config", "user.name", "t");
        await GitAsync(_repo, "config", "commit.gpgsign", "false");

        Put("README.md", "readme-v1\n");
        Put("backend/.env", $"{V1}\n");
        Put("backend/src/app.py", "print('v1')\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "first");

        Put("README.md", "readme-v1\nvisible-readme-change\n");
        Put("backend/.env", $"{V2}\n");
        Put("backend/src/app.py", "print('v2')\n");
    }

    private ShellPlugin Shell(bool withDenyRules = true) => new(
        _repo, denyPatterns: withDenyRules ? DefaultSecurityPolicy.MergeFileSystemDeny(null) : null);

    private static void AssertNoSecrets(string result)
    {
        Assert.DoesNotContain(V1, result);
        Assert.DoesNotContain(V2, result);
    }

    [Theory]
    [InlineData("git diff")]
    [InlineData("git diff HEAD")]
    [InlineData("git diff --no-color -U5")]
    [InlineData("git log -p")]
    [InlineData("git log -p --all")]
    [InlineData("git show HEAD")]
    [InlineData("git diff | cat")]
    [InlineData("git diff 2>&1")]
    [InlineData("cd backend && git diff")]                  // run from a sub-directory of the repo
    public async Task ShellRun_HidesTheProtectedFilesBody_ButKeepsTheOrdinaryOnes(string command)
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell();

        var result = await shell.RunAsync(command);

        AssertNoSecrets(result);
        Assert.Contains("[content hidden: ", result);
        Assert.Contains("matches a FileSystem deny rule]", result);
    }

    [Fact]
    public async Task ShellRun_StillShowsAnOrdinaryFilesChanges()
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell();

        var result = await shell.RunAsync("git diff");

        Assert.Contains("+visible-readme-change", result);
        Assert.Contains("+print('v2')", result);
        Assert.Contains("diff --git a/backend/.env b/backend/.env", result);      // the agent can still see it changed
    }

    [Fact]
    public async Task ShellRun_FromASubdirectory_HidesTheFileToo()
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell();

        var result = await shell.RunAsync("git diff", Path.Combine(_repo, "backend"));

        AssertNoSecrets(result);
        Assert.Contains("[content hidden: ", result);
    }

    // `**/.env` matches at any depth, so it can't tell a right repository top-level from a wrong one; a
    // pattern anchored to the sandbox root can.
    [Fact]
    public async Task ShellRun_FromASubdirectory_ResolvesDiffPathsAgainstTheRepositoryRoot()
    {
        await GitAsync(_repo, "init", "-q");
        await GitAsync(_repo, "config", "user.email", "t@example.com");
        await GitAsync(_repo, "config", "user.name", "t");
        await GitAsync(_repo, "config", "commit.gpgsign", "false");
        Put("backend/secrets.txt", $"{V1}\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "first");
        Put("backend/secrets.txt", $"{V2}\n");
        using var shell = new ShellPlugin(_repo, denyPatterns: ["backend/secrets.txt"]);

        var result = await shell.RunAsync("git diff", Path.Combine(_repo, "backend"));

        AssertNoSecrets(result);
        Assert.Contains("[content hidden: 'backend/secrets.txt'", result);
    }

    [Fact]
    public async Task ShellRun_WithNoDenyRules_ShowsTheSecrets_SoTheFilterIsWhatHidesThem()
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell(withDenyRules: false);

        var result = await shell.RunAsync("git diff");

        Assert.Contains(V2, result);
    }

    [Fact]
    public async Task ShellRunScript_HidesTheProtectedFilesBody()
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell();

        var result = await shell.RunScriptAsync("git status --short\ngit diff\n", _repo);

        AssertNoSecrets(result);
        Assert.Contains("[content hidden: ", result);
    }

    [Fact]
    public async Task BackgroundJob_HidesTheProtectedFilesBody()
    {
        await InitRepoWithTrackedSecretAsync();
        using var shell = Shell();

        var started = await shell.RunBackgroundAsync("git diff", _repo);
        var jobId = System.Text.RegularExpressions.Regex.Match(started, @"Job ID: (\w+)").Groups[1].Value;
        Assert.NotEmpty(jobId);
        await Task.Delay(500);

        var output = await shell.GetJobOutput(jobId);
        var status = await shell.GetJobStatus(jobId);

        AssertNoSecrets(output);
        AssertNoSecrets(status);
        Assert.Contains("[content hidden: ", output);
    }

    [Fact]
    public async Task Probe_HidesTheProtectedFilesBody()
    {
        await InitRepoWithTrackedSecretAsync();
        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { FileSystemSandboxPath = _repo });
        Assert.True(registry.TryGet("Probe", out var obj));
        var probe = Assert.IsType<ProbePlugin>(obj);

        var output = await probe.ProbeCodeAsync("bash", "git diff", _repo);

        AssertNoSecrets(output);
        Assert.Contains("[content hidden: ", output);
    }

    [Fact]
    public async Task ShellRun_OutsideARepository_LeavesOutputAlone()
    {
        Put("notes.txt", "no repository here\n");
        using var shell = Shell();

        var result = await shell.RunAsync("cat notes.txt");

        Assert.Contains("no repository here", result);
    }

    [Fact]
    public async Task ShellRun_CatOfAPatchFile_HidesAProtectedSectionToo()
    {
        Put("changes.patch",
            "diff --git a/.env b/.env\nindex 111..222 100644\n--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-" + V1 + "\n+" + V2 + "\n");
        using var shell = Shell();

        var result = await shell.RunAsync("cat changes.patch");

        AssertNoSecrets(result);
        Assert.Contains("[content hidden: '.env'", result);
    }

    [Fact]
    public void ScrubOutput_MasksAndFilters_InOneCall()
    {
        using var shell = Shell();
        var patch = "diff --git a/.env b/.env\n--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-" + V1 + "\n+" + V2 + "\n";

        var result = shell.ScrubOutput(patch, _repo);

        AssertNoSecrets(result);
    }
}
