using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The Search plugin enumerates files itself, so FileSystem deny rules (<c>.env</c>, credentials
/// files, configured <c>Deny</c> globs) never reached it: searching a project for a secret's name
/// returned the matching line of a <c>.env</c> that <c>read_file</c> refuses to open — no approval,
/// no prompt. Found by driving the real REPL against a fake nested <c>backend/.env</c> and asking
/// <c>search_content</c> for the secret's key. Every search tool must skip a denied file.
/// </summary>
public sealed class SearchPluginDenyRuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft_searchdeny_").FullName;

    private const string Token = "ZZ_TOKEN_MARKER";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private SearchPlugin Plugin(FileSystemPermissions? permissions = null, bool denyCredentialFiles = true) =>
        new(sandboxRoot: _dir, denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(permissions, denyCredentialFiles));

    public static TheoryData<string> ProtectedFiles => new()
    {
        ".env", "backend/.env", "apps/web/.env.local", "deploy/id_rsa", "home/u/.aws/credentials", ".netrc", ".pgpass",
    };

    [Theory, MemberData(nameof(ProtectedFiles))]
    public void SearchContent_DoesNotReturnLinesFromAProtectedFile(string relative)
    {
        Write(relative, $"{Token}=super-secret-value");
        Write("src/app.py", $"# mentions {Token} in ordinary code");

        var result = Plugin().SearchContent(Token, _dir);

        Assert.Contains("src", result);                                   // ordinary file still found
        Assert.DoesNotContain("super-secret-value", result);
        Assert.DoesNotContain(Path.GetFileName(relative), result.Replace("app.py", ""));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public void SearchContent_WhenOnlyAProtectedFileMatches_FindsNothing(string relative)
    {
        Write(relative, $"{Token}=super-secret-value");

        var result = Plugin().SearchContent(Token, _dir);

        Assert.Contains("No matches found", result);
        Assert.DoesNotContain("super-secret-value", result);
    }

    [Fact]
    public void SearchContent_WithNoDenyPatterns_FindsTheSameFile_SoTheRuleIsWhatHidesIt()
    {
        Write("backend/.env", $"{Token}=super-secret-value");

        var result = new SearchPlugin(sandboxRoot: _dir).SearchContent(Token, _dir);

        Assert.Contains("super-secret-value", result);       // baseline: nothing else excludes it
    }

    [Fact]
    public void SearchCallers_SkipsAProtectedFile()
    {
        Write("backend/.env", $"call {Token}(1)");
        Write("src/app.py", $"x = {Token}(2)");

        var result = Plugin().SearchCallers(Token, _dir);

        Assert.DoesNotContain(".env", result);
        Assert.Contains("app.py", result);
    }

    [Fact]
    public void SearchSymbol_SkipsAProtectedFile()
    {
        Write("backend/.env", $"class {Token}");
        Write("src/app.py", $"class {Token}");

        var result = Plugin().SearchSymbol(Token, _dir);

        Assert.DoesNotContain(".env", result);
        Assert.Contains("app.py", result);
    }

    [Fact]
    public void SearchContent_HonoursAConfiguredDenyGlobToo()
    {
        Write("secrets/api.txt", $"{Token}=configured-secret");

        var result = Plugin(new FileSystemPermissions { Deny = ["secrets/**"] }).SearchContent(Token, _dir);

        Assert.Contains("No matches found", result);
    }

    [Fact]
    public void SearchContent_OptedOutOfCredentialFiles_FindsThemButStillSkipsEnv()
    {
        Write("deploy/id_rsa", $"{Token}=key-material");
        Write("backend/.env", $"{Token}=env-value");

        var result = Plugin(denyCredentialFiles: false).SearchContent(Token, _dir);

        Assert.Contains("key-material", result);
        Assert.DoesNotContain("env-value", result);
    }

    [Fact]
    public void SearchContent_PublicKeysAndOrdinaryFilesAreStillSearchable()
    {
        Write("deploy/id_rsa.pub", $"ssh-ed25519 {Token} user@host");
        Write("config.yaml", $"key: {Token}");

        var result = Plugin().SearchContent(Token, _dir);

        Assert.Contains("id_rsa.pub", result);
        Assert.Contains("config.yaml", result);
    }

    [Fact]
    public void SearchContent_Unsandboxed_StillSkipsDirectoryQualifiedCredentialPaths()
    {
        // No sandbox root (--yolo): the matcher used to see only file names.
        Write("home/u/.aws/credentials", $"{Token}=aws-secret");

        var plugin = new SearchPlugin(sandboxRoot: null, denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(null));
        var result = plugin.SearchContent(Token, _dir);

        Assert.Contains("No matches found", result);
    }

    [Fact]
    public void SearchContent_SearchingDirectlyInsideAProtectedDirectory_StillSkipsIt()
    {
        var full = Write("home/u/.aws/credentials", $"{Token}=aws-secret");

        var result = Plugin().SearchContent(Token, Path.GetDirectoryName(full)!);

        Assert.Contains("No matches found", result);
    }

    [Fact]
    public void Configure_WiresTheDenyRulesIntoTheRegisteredSearchPlugin()
    {
        Write("backend/.env", $"{Token}=super-secret-value");
        Write("src/app.py", $"# {Token}");

        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { FileSystemSandboxPath = _dir });
        Assert.True(registry.TryGet("Search", out var obj));
        var search = Assert.IsType<SearchPlugin>(obj);

        var result = search.SearchContent(Token, _dir);

        Assert.DoesNotContain("super-secret-value", result);
        Assert.Contains("app.py", result);
    }

    [Fact]
    public void Configure_OptOutReachesTheRegisteredSearchPlugin()
    {
        Write("deploy/id_rsa", $"{Token}=key-material");

        var registry = new PluginRegistry().RegisterDefaults()
            .Configure(new SecurityConfig { FileSystemSandboxPath = _dir, DenyCredentialFiles = false });
        Assert.True(registry.TryGet("Search", out var obj));

        var result = Assert.IsType<SearchPlugin>(obj).SearchContent(Token, _dir);

        Assert.Contains("key-material", result);
    }
}
