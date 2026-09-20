using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The Git tools return file content — a diff is content — so a tracked <c>.env</c> or credentials
/// file that <c>read_file</c> refuses to open used to come straight back through <c>git_show</c> and
/// <c>git_diff</c>. Found by probing the real plugin after the FileSystem and Search plugins were
/// fixed. These run against real repositories with real <c>git</c>.
/// </summary>
public sealed class GitPluginDenyRuleTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("fuseraft_gitdeny_").FullName;

    private const string V1 = "SECRET-V1-marker";
    private const string V2 = "SECRET-V2-marker";

    public void Dispose()
    {
        // git marks some objects read-only on Windows; harmless elsewhere.
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_repo, recursive: true);
    }

    private static async Task GitAsync(string dir, params string[] args)
    {
        var r = await ProcessHelper.RunAsync("git", args, dir);
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)} failed: {r.Stderr}");
    }

    private static async Task<string> GitOutAsync(string dir, params string[] args)
    {
        var r = await ProcessHelper.RunAsync("git", args, dir);
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)} failed: {r.Stderr}");
        return r.Stdout.Trim();
    }

    private void Put(string relative, string content)
    {
        var full = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static readonly string[] ProtectedFiles =
        ["backend/.env", "deploy/id_rsa", "home/u/.aws/credentials", "sub/.env.local", ".netrc"];

    // Commit 1 tracks everything with V1 secrets; the working tree then changes every file to V2
    // (README to a new visible line), leaving the changes UNSTAGED.
    private async Task<GitPlugin> RepoWithTrackedSecretsAsync(
        IReadOnlyList<string>? denyPatterns = null, bool useDefaultDeny = true)
    {
        await GitAsync(_repo, "init", "-q");
        await GitAsync(_repo, "config", "user.email", "t@example.com");
        await GitAsync(_repo, "config", "user.name", "t");
        await GitAsync(_repo, "config", "commit.gpgsign", "false");

        Put("README.md", "readme-v1\n");
        Put("src/app.py", "print('v1')\n");
        foreach (var f in ProtectedFiles) Put(f, $"{V1}\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "first");

        Put("README.md", "readme-v1\nvisible-readme-change\n");
        Put("src/app.py", "print('v2')\n");
        foreach (var f in ProtectedFiles) Put(f, $"{V2}\n");

        var deny = denyPatterns ?? (useDefaultDeny ? DefaultSecurityPolicy.MergeFileSystemDeny(null) : null);
        return new GitPlugin(sandboxRoot: _repo, denyPatterns: deny);
    }

    private static void AssertNoSecrets(string result)
    {
        Assert.DoesNotContain(V1, result);
        Assert.DoesNotContain(V2, result);
    }

    // -- git_diff ------------------------------------------------------------------------------

    [Fact]
    public async Task Diff_HidesEveryProtectedFilesChanges_ButShowsTheOrdinaryOnes()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.DiffAsync();

        AssertNoSecrets(result);
        Assert.Contains("+visible-readme-change", result);
        Assert.Contains("+print('v2')", result);
        foreach (var f in ProtectedFiles)
        {
            Assert.Contains($"diff --git a/{f} b/{f}", result);                       // the agent can still see it changed
            Assert.Contains($"[content hidden: '{f}' matches a FileSystem deny rule]", result);
        }
    }

    [Fact]
    public async Task Diff_Staged_HidesProtectedChangesToo()
    {
        var git = await RepoWithTrackedSecretsAsync();
        await GitAsync(_repo, "add", "-A");

        var result = await git.DiffAsync(staged: true);

        AssertNoSecrets(result);
        Assert.Contains("+visible-readme-change", result);
    }

    [Fact]
    public async Task Diff_WithNoDenyPatterns_ShowsTheSecrets_SoTheFilterIsWhatHidesThem()
    {
        var git = await RepoWithTrackedSecretsAsync(useDefaultDeny: false);

        var result = await git.DiffAsync();

        Assert.Contains(V2, result);
    }

    [Fact]
    public async Task Diff_MaxLines_CountsTheFilteredOutput()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.DiffAsync(maxLines: 10_000);

        Assert.DoesNotContain("lines truncated", result);
    }

    [Fact]
    public async Task Diff_InANonRepository_DoesNotThrowWhenDenyRulesAreSet()
    {
        var plain = Directory.CreateTempSubdirectory("fuseraft_notgit_").FullName;
        try
        {
            var git = new GitPlugin(sandboxRoot: plain, denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(null));

            var result = await git.DiffAsync();

            Assert.DoesNotContain(V1, result);
        }
        finally { Directory.Delete(plain, recursive: true); }
    }

    // -- git_show: patches ------------------------------------------------------------------------

    [Fact]
    public async Task Show_OfACommitThatAddedProtectedFiles_HidesTheirContents()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.ShowAsync("HEAD");

        AssertNoSecrets(result);
        Assert.Contains("readme-v1", result);
        Assert.Contains("first", result);                       // the commit message is untouched
        Assert.Contains("[content hidden: 'backend/.env'", result);
    }

    [Fact]
    public async Task Show_ByCommitHash_IsFilteredToo()
    {
        var git = await RepoWithTrackedSecretsAsync();
        var sha = await GitOutAsync(_repo, "rev-parse", "HEAD");

        AssertNoSecrets(await git.ShowAsync(sha));
    }

    [Fact]
    public async Task Show_WithAPathspec_StillHidesTheSection()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.ShowAsync("HEAD -- backend/.env");

        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Show_WithAnOptionAndACommit_StillWorks()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.ShowAsync("HEAD --stat");

        Assert.Contains("README.md", result);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Log_GivenPatchAsItsRef_IsFilteredToo()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.LogAsync(@ref: "-p");

        AssertNoSecrets(result);
        Assert.Contains("first", result);
    }

    // -- git_show: blobs by path ----------------------------------------------------------------

    [Theory]
    [InlineData("HEAD:backend/.env")]
    [InlineData("HEAD:deploy/id_rsa")]
    [InlineData("HEAD:home/u/.aws/credentials")]
    [InlineData("HEAD:sub/.env.local")]
    [InlineData("HEAD:.netrc")]
    [InlineData("HEAD~0:backend/.env")]
    [InlineData("HEAD@{0}:backend/.env")]
    [InlineData(":backend/.env")]
    [InlineData(":0:backend/.env")]
    [InlineData("--no-patch HEAD:backend/.env")]
    public async Task Show_OfAProtectedBlobByPath_IsDenied(string target)
    {
        var git = await RepoWithTrackedSecretsAsync();
        await GitAsync(_repo, "add", "-A");                     // so the `:path` index forms resolve

        var result = await git.ShowAsync(target);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("deny rule", result);
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData("HEAD:README.md")]
    [InlineData("HEAD:src/app.py")]
    [InlineData("HEAD:deploy/id_rsa.pub")]
    public async Task Show_OfAnOrdinaryBlobByPath_StillWorks(string target)
    {
        var git = await RepoWithTrackedSecretsAsync();
        Put("deploy/id_rsa.pub", "ssh-ed25519 AAAA public\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "add public key");

        var result = await git.ShowAsync(target.Replace("HEAD:", "HEAD:"));

        Assert.DoesNotContain("[DENIED]", result);
    }

    [Fact]
    public async Task Show_OfADirectoryTree_ListsNamesOnly_AndIsAllowed()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.ShowAsync("HEAD:backend");

        Assert.DoesNotContain("[DENIED]", result);
        Assert.DoesNotContain(V1, result);                      // a tree listing has names, not content
    }

    [Fact]
    public async Task Show_ARelativeBlobPath_IsCheckedAgainstTheWorkingDirectoryToo()
    {
        var git = await RepoWithTrackedSecretsAsync();
        var backend = Path.Combine(_repo, "backend");

        var viaCwd = await git.ShowAsync("HEAD:./.env", repoPath: backend);
        var viaTop = await git.ShowAsync("HEAD:backend/.env", repoPath: backend);

        Assert.StartsWith("[DENIED]", viaCwd);
        Assert.StartsWith("[DENIED]", viaTop);
        AssertNoSecrets(viaCwd + viaTop);
    }

    // -- git_show: bare blob hashes ---------------------------------------------------------------

    [Fact]
    public async Task Show_OfAProtectedBlobsBareHash_IsDenied_BecauseItHasNoPath()
    {
        var git = await RepoWithTrackedSecretsAsync();
        var blob = await GitOutAsync(_repo, "rev-parse", "HEAD:backend/.env");

        var result = await git.ShowAsync(blob);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("bare blob", result);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Show_OfAnyBareBlobHash_IsRefused_WithGuidanceToUseAPath()
    {
        var git = await RepoWithTrackedSecretsAsync();
        var blob = await GitOutAsync(_repo, "rev-parse", "HEAD:README.md");

        var result = await git.ShowAsync(blob);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("git show HEAD:<path>", result);
    }

    [Fact]
    public async Task Show_OfAnAbbreviatedProtectedBlobHash_IsDeniedToo()
    {
        var git = await RepoWithTrackedSecretsAsync();
        var blob = (await GitOutAsync(_repo, "rev-parse", "--short=7", "HEAD:deploy/id_rsa"));

        AssertNoSecrets(await git.ShowAsync(blob));
    }

    // -- configuration ----------------------------------------------------------------------------

    [Fact]
    public async Task Show_WithNoDenyPatterns_ReturnsBlobsAndPatchesAsBefore()
    {
        var git = await RepoWithTrackedSecretsAsync(useDefaultDeny: false);

        Assert.Contains(V1, await git.ShowAsync("HEAD:backend/.env"));
        Assert.Contains(V1, await git.ShowAsync("HEAD"));
    }

    [Fact]
    public async Task OptedOutOfCredentialFiles_ShowsAnSshKeyButStillHidesEnv()
    {
        var git = await RepoWithTrackedSecretsAsync(
            denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(null, denyCredentialFiles: false));

        var diff = await git.DiffAsync();

        Assert.Contains("[content hidden: 'backend/.env'", diff);
        Assert.DoesNotContain("[content hidden: 'deploy/id_rsa'", diff);
        Assert.StartsWith("[DENIED]", await git.ShowAsync("HEAD:backend/.env"));
        Assert.DoesNotContain("[DENIED]", await git.ShowAsync("HEAD:deploy/id_rsa"));
    }

    [Fact]
    public async Task AConfiguredDenyGlob_AppliesToGitOutputToo()
    {
        await GitAsync(_repo, "init", "-q");
        await GitAsync(_repo, "config", "user.email", "t@example.com");
        await GitAsync(_repo, "config", "user.name", "t");
        Put("secrets/api.txt", "configured-v1\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "x");
        Put("secrets/api.txt", "configured-v2-marker\n");
        var git = new GitPlugin(sandboxRoot: _repo,
            denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(new FileSystemPermissions { Deny = ["secrets/**"] }));

        var result = await git.DiffAsync();

        Assert.DoesNotContain("configured-v2-marker", result);
        Assert.StartsWith("[DENIED]", await git.ShowAsync("HEAD:secrets/api.txt"));
    }

    [Fact]
    public async Task Configure_WiresTheDenyRulesIntoTheRegisteredGitPlugin()
    {
        await RepoWithTrackedSecretsAsync(useDefaultDeny: false);

        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { FileSystemSandboxPath = _repo });
        Assert.True(registry.TryGet("Git", out var obj));
        var git = Assert.IsType<GitPlugin>(obj);

        AssertNoSecrets(await git.DiffAsync());
        AssertNoSecrets(await git.ShowAsync("HEAD"));
        Assert.StartsWith("[DENIED]", await git.ShowAsync("HEAD:backend/.env"));
    }

    [Fact]
    public async Task Configure_OptOut_ReachesTheRegisteredGitPlugin()
    {
        await RepoWithTrackedSecretsAsync(useDefaultDeny: false);

        var registry = new PluginRegistry().RegisterDefaults()
            .Configure(new SecurityConfig { FileSystemSandboxPath = _repo, DenyCredentialFiles = false });
        Assert.True(registry.TryGet("Git", out var obj));

        var diff = await Assert.IsType<GitPlugin>(obj).DiffAsync();

        Assert.DoesNotContain("[content hidden: 'deploy/id_rsa'", diff);
    }

    // -- what must NOT change ----------------------------------------------------------------------

    [Fact]
    public async Task Status_StillListsProtectedFilesByName_NamesAreNotContent()
    {
        var git = await RepoWithTrackedSecretsAsync();

        var result = await git.StatusAsync();

        Assert.Contains("backend/.env", result);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task OrdinaryFileHistory_IsUnaffected()
    {
        var git = await RepoWithTrackedSecretsAsync();
        await GitAsync(_repo, "add", "README.md");
        await GitAsync(_repo, "commit", "-qm", "readme only");

        var result = await git.ShowAsync("HEAD");

        Assert.Contains("+visible-readme-change", result);
        Assert.DoesNotContain("content hidden", result);
    }
}
