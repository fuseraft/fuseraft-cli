using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <c>git_show</c>'s <c>commitRef</c> and <c>git_log</c>'s <c>ref</c> were spliced into the git command
/// line, so <c>HEAD --output=leak.txt</c> made git write the <i>unfiltered</i> patch — a tracked
/// <c>.env</c> included — to a file the agent chose (verified with the git CLI before this fix), where
/// <c>read_file</c> could open it. It is also a write outside the sandbox from a tool nobody gates.
/// Options are now an allowlist, and every argument reaches git as its own argv element.
/// </summary>
public sealed class GitPluginArgumentInjectionTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("fuseraft_gitinject_").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("fuseraft_gitinject_out_").FullName;

    private const string Secret = "SECRET-INJECT-marker";

    public void Dispose()
    {
        foreach (var dir in new[] { _repo, _outside })
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task GitAsync(string dir, params string[] args)
    {
        var r = await ProcessHelper.RunAsync("git", args, dir);
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)} failed: {r.Stderr}");
    }

    private async Task<GitPlugin> RepoAsync(bool withDenyRules = true)
    {
        await GitAsync(_repo, "init", "-q");
        await GitAsync(_repo, "config", "user.email", "t@example.com");
        await GitAsync(_repo, "config", "user.name", "t");
        await GitAsync(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "readme\n");
        File.WriteAllText(Path.Combine(_repo, ".env"), $"{Secret}\n");
        await GitAsync(_repo, "add", "-A");
        await GitAsync(_repo, "commit", "-qm", "first");

        return new GitPlugin(
            sandboxRoot: _repo,
            denyPatterns: withDenyRules ? DefaultSecurityPolicy.MergeFileSystemDeny(null) : null);
    }

    private void AssertNothingWritten(params string[] names)
    {
        foreach (var name in names)
        {
            Assert.False(File.Exists(Path.Combine(_repo, name)), $"{name} was written inside the repo");
            Assert.False(File.Exists(Path.Combine(_outside, name)), $"{name} was written outside the sandbox");
        }
    }

    [Theory]
    [InlineData("HEAD --output=leak.txt")]
    [InlineData("HEAD --output leak.txt")]
    [InlineData("--output=leak.txt HEAD")]
    [InlineData("HEAD --stat --output=leak.txt")]
    [InlineData("HEAD\n--output=leak.txt")]                 // any whitespace splits, not just a space
    [InlineData("HEAD\t--output=leak.txt")]
    public async Task ShowAsync_RefusesAnOptionThatWritesAFile(string commitRef)
    {
        var git = await RepoAsync();

        var result = await git.ShowAsync(commitRef);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("--output", result);
        AssertNothingWritten("leak.txt");
    }

    [Fact]
    public async Task ShowAsync_RefusesAWriteOutsideTheSandbox()
    {
        var git = await RepoAsync();
        var target = Path.Combine(_outside, "escaped.txt");

        var result = await git.ShowAsync($"HEAD --output={target}");

        Assert.StartsWith("[DENIED]", result);
        AssertNothingWritten("escaped.txt");
    }

    [Fact]
    public async Task ShowAsync_RefusesTheWriteEvenWhenNoDenyRulesAreConfigured()
    {
        var git = await RepoAsync(withDenyRules: false);

        var result = await git.ShowAsync("HEAD --output=leak.txt");

        Assert.StartsWith("[DENIED]", result);
        AssertNothingWritten("leak.txt");
    }

    [Theory]
    [InlineData("-p --output=leak.txt")]
    [InlineData("--output=leak.txt")]
    [InlineData("--all --output leak.txt")]
    public async Task LogAsync_RefusesAnOptionThatWritesAFile(string @ref)
    {
        var git = await RepoAsync();

        var result = await git.LogAsync(@ref: @ref);

        Assert.StartsWith("[DENIED]", result);
        AssertNothingWritten("leak.txt");
    }

    [Theory]
    [InlineData("HEAD --ext-diff")]
    [InlineData("HEAD --open-files-in-pager=cat")]
    [InlineData("HEAD -Oorderfile")]
    [InlineData("HEAD --exec-path=/tmp")]
    [InlineData("HEAD --textconv")]
    [InlineData("HEAD --no-such-option")]
    public async Task ShowAsync_RefusesOptionsThatCanRunAProgramOrAreUnknown(string commitRef)
    {
        var git = await RepoAsync();

        Assert.StartsWith("[DENIED]", await git.ShowAsync(commitRef));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("HEAD --stat")]
    [InlineData("HEAD -p")]
    [InlineData("HEAD --name-only")]
    [InlineData("HEAD --name-status")]
    [InlineData("HEAD -U5")]
    [InlineData("HEAD --unified=2")]
    [InlineData("HEAD --oneline --no-patch")]
    [InlineData("HEAD --pretty=format:%H")]
    [InlineData("HEAD -- README.md")]
    public async Task ShowAsync_StillAcceptsOrdinaryDisplayOptions(string commitRef)
    {
        var git = await RepoAsync();

        var result = await git.ShowAsync(commitRef);

        Assert.DoesNotContain("[DENIED]", result);
        Assert.DoesNotContain("[ERROR]", result);
        Assert.DoesNotContain(Secret, result);                // the deny-rule filter still applies to what is shown
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HEAD")]
    [InlineData("--all")]
    [InlineData("-p")]
    [InlineData("--stat --max-count=1")]
    [InlineData("--author=t")]
    [InlineData("--since=2000-01-01")]
    public async Task LogAsync_StillAcceptsOrdinaryRefsAndOptions(string? @ref)
    {
        var git = await RepoAsync();

        var result = await git.LogAsync(@ref: @ref);

        Assert.DoesNotContain("[DENIED]", result);
        Assert.DoesNotContain("[ERROR]", result);
        Assert.Contains("first", result);
        Assert.DoesNotContain(Secret, result);
    }

    // After `--` everything is a pathspec, so a dashed name is a file name, not an option.
    [Fact]
    public async Task ShowAsync_AfterDoubleDash_ADashedNameIsJustAPathspec()
    {
        var git = await RepoAsync();

        var result = await git.ShowAsync("HEAD -- --output=leak.txt");

        Assert.DoesNotContain("[DENIED]", result);
        AssertNothingWritten("leak.txt");
    }

    [Fact]
    public async Task ShowAsync_TheBlobPathCheckStillApplies()
    {
        var git = await RepoAsync();

        var result = await git.ShowAsync("HEAD:.env");

        Assert.StartsWith("[DENIED]", result);
        Assert.DoesNotContain(Secret, result);
    }
}
