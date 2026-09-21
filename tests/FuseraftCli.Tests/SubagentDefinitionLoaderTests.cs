using fuseraft.Core.Subagents;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="SubagentDefinitionLoader"/>: the Markdown-file format for user-defined
/// subagents. The rule that matters most is that one bad file is reported and skipped, never
/// allowed to hide the good ones — and that anything the format does not honour is said out loud
/// rather than silently ignored.
/// </summary>
public sealed class SubagentDefinitionLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuseraft-agents-" + Guid.NewGuid().ToString("N"));

    public SubagentDefinitionLoaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static (SubagentDefinition? Def, List<string> Problems) Parse(string content, string path = "/x/reviewer.md", string scope = "project")
    {
        var problems = new List<string>();
        SubagentDefinitionLoader.TryParse(path, scope, content, out var def, problems);
        return (def, problems);
    }

    private const string Valid = """
        ---
        name: reviewer
        description: Reviews diffs for correctness bugs.
        ---
        You are a meticulous code reviewer.
        """;

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_NameDescriptionAndBody()
    {
        var (def, problems) = Parse(Valid);

        Assert.NotNull(def);
        Assert.Empty(problems);
        Assert.Equal("reviewer", def!.Name);
        Assert.Equal("Reviews diffs for correctness bugs.", def.Description);
        Assert.Equal("You are a meticulous code reviewer.", def.Instructions);
        Assert.Null(def.Model);
        Assert.Null(def.Tools);
        Assert.Equal(SubagentDefinitionLoader.DefaultMaxIterations, def.MaxIterations);
        Assert.Equal("project", def.Scope);
    }

    [Fact]
    public void NameFallsBackToTheFileName()
    {
        var (def, _) = Parse("---\ndescription: d\n---\nbody", path: "/x/test-writer.md");

        Assert.Equal("test-writer", def!.Name);
    }

    [Fact]
    public void ToleratesCrlfBomAndLeadingBlankLines()
    {
        var (def, problems) = Parse("﻿\r\n\r\n---\r\nname: crlf\r\ndescription: d\r\n---\r\nline one\r\nline two\r\n");

        Assert.NotNull(def);
        Assert.Empty(problems);
        Assert.Equal("line one\nline two", def!.Instructions);
    }

    [Fact]
    public void FieldNamesAreCaseInsensitive()
    {
        var (def, problems) = Parse("---\nName: cased\nDescription: d\nTools: [read_file]\nMODEL: gpt-x\n---\nbody");

        Assert.Empty(problems);
        Assert.Equal("cased", def!.Name);
        Assert.Equal(["read_file"], def.Tools);
        Assert.Equal("gpt-x", def.Model);
    }

    [Fact]
    public void KeepsColonsAndMarkdownInTheBody()
    {
        var (def, _) = Parse("---\nname: a\ndescription: d\n---\n# Title\n\nSteps:\n1. do x\n\n---\nnot frontmatter: still body\n");

        Assert.Contains("# Title", def!.Instructions);
        Assert.Contains("not frontmatter: still body", def.Instructions);
    }

    // ── tools ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Tools_OmittedMeansDefaultReadOnly_NotEmpty()
    {
        Assert.Null(Parse(Valid).Def!.Tools);
    }

    [Fact]
    public void Tools_ListCommaStringAndDuplicatesNormalise()
    {
        Assert.Equal(["read_file", "grep_file"], Parse("---\nname: a\ndescription: d\ntools: [read_file, grep_file, READ_FILE]\n---\nb").Def!.Tools);
        Assert.Equal(["read_file", "grep_file"], Parse("---\nname: a\ndescription: d\ntools: read_file, grep_file\n---\nb").Def!.Tools);
        Assert.Equal(["read_file", "grep_file"], Parse("---\nname: a\ndescription: d\ntools:\n  - read_file\n  - grep_file\n---\nb").Def!.Tools);
    }

    [Fact]
    public void Tools_ExplicitlyEmpty_IsNoTools_NotTheDefault()
    {
        Assert.Empty(Parse("---\nname: a\ndescription: d\ntools: []\n---\nb").Def!.Tools!);
        Assert.Empty(Parse("---\nname: a\ndescription: d\ntools:\n---\nb").Def!.Tools!);
    }

    [Fact]
    public void Tools_WildcardGrantsAll()
    {
        var def = Parse("---\nname: a\ndescription: d\ntools: ['*']\n---\nb").Def!;

        Assert.True(def.AllTools);
        Assert.False(Parse(Valid).Def!.AllTools);
    }

    // ── max_iterations ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12", 12, false)]
    [InlineData("1", 1, false)]
    [InlineData("100", 100, false)]
    [InlineData("0", SubagentDefinitionLoader.DefaultMaxIterations, true)]
    [InlineData("101", SubagentDefinitionLoader.DefaultMaxIterations, true)]
    [InlineData("many", SubagentDefinitionLoader.DefaultMaxIterations, true)]
    [InlineData("-4", SubagentDefinitionLoader.DefaultMaxIterations, true)]
    public void MaxIterations_IsValidated_AndFallsBackWithAWarning(string raw, int expected, bool warns)
    {
        var (def, problems) = Parse($"---\nname: a\ndescription: d\nmax_iterations: {raw}\n---\nb");

        Assert.NotNull(def);                       // a bad number never costs the whole agent
        Assert.Equal(expected, def!.MaxIterations);
        Assert.Equal(warns, problems.Any(p => p.Contains("max_iterations")));
    }

    [Fact]
    public void MaxIterations_AcceptsTheOpenHandsSpelling()
    {
        Assert.Equal(7, Parse("---\nname: a\ndescription: d\nmax_iteration_per_run: 7\n---\nb").Def!.MaxIterations);
    }

    // ── Rejections ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("just a markdown file\nno frontmatter", "no YAML frontmatter")]
    [InlineData("---\nname: a\ndescription: d\nbody without closing fence", "no YAML frontmatter")]
    [InlineData("---\nname: a\n---\nbody", "'description' is required")]
    [InlineData("---\nname: a\ndescription: \"  \"\n---\nbody", "'description' is required")]
    [InlineData("---\nname: a\ndescription: d\n---\n   \n", "body is empty")]
    [InlineData("---\nname: Not Valid!\ndescription: d\n---\nbody", "must be lowercase")]
    [InlineData("---\nname: UPPER\ndescription: d\n---\nbody", "must be lowercase")]
    [InlineData("---\nname: ../evil\ndescription: d\n---\nbody", "must be lowercase")]
    [InlineData("---\nname: a\n  bad: [unclosed\n---\nbody", "invalid frontmatter YAML")]
    public void Rejects_AndSaysWhy(string content, string reason)
    {
        var (def, problems) = Parse(content);

        Assert.Null(def);
        Assert.Contains(problems, p => p.Contains(reason));
        Assert.All(problems, p => Assert.StartsWith("/x/reviewer.md:", p));   // always names the file
    }

    [Fact]
    public void KeysDifferingOnlyByCase_AreRejectedAsAmbiguous()
    {
        var (def, problems) = Parse("---\nname: a\nName: b\ndescription: d\n---\nbody");

        Assert.Null(def);
        Assert.Contains(problems, p => p.Contains("invalid frontmatter"));
    }

    [Fact]
    public void UnsupportedFields_AreReported_NotSilentlyIgnored()
    {
        var (def, problems) = Parse("---\nname: a\ndescription: d\npermission_mode: always_confirm\nhooks: {}\ncolor: red\n---\nbody");

        Assert.NotNull(def);                       // still usable
        var p = Assert.Single(problems);
        Assert.Contains("permission_mode", p);
        Assert.Contains("hooks", p);
        Assert.DoesNotContain("color", p);         // accepted for cross-tool compatibility
    }

    // ── Directory discovery ─────────────────────────────────────────────────────

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteAgent(string dir, string file, string name, string description = "d", string body = "body") =>
        File.WriteAllText(Path.Combine(dir, file), $"---\nname: {name}\ndescription: {description}\n---\n{body}");

    [Fact]
    public void OneBadFile_DoesNotHideTheGoodOnes()
    {
        var d = Dir("agents");
        WriteAgent(d, "good.md", "good");
        File.WriteAllText(Path.Combine(d, "bad.md"), "not an agent");
        WriteAgent(d, "also-good.md", "also-good");

        var r = SubagentDefinitionLoader.LoadFromDirectories([(d, "project")]);

        Assert.Equal(["also-good", "good"], r.Definitions.Select(x => x.Name).OrderBy(x => x));
        Assert.Contains(r.Problems, p => p.Contains("bad.md"));
    }

    [Fact]
    public void ProjectScopeWinsOverUser_AndTheShadowedFileIsReported()
    {
        var project = Dir("project");
        var user    = Dir("user");
        WriteAgent(project, "reviewer.md", "reviewer", description: "project version");
        WriteAgent(user,    "reviewer.md", "reviewer", description: "user version");

        var r = SubagentDefinitionLoader.LoadFromDirectories([(project, "project"), (user, "user")]);

        var only = Assert.Single(r.Definitions);
        Assert.Equal("project version", only.Description);
        Assert.Equal("project", only.Scope);
        Assert.Contains(r.Problems, p => p.Contains("shadowed") && p.Contains(user));
    }

    [Fact]
    public void SameNameIsShadowedCaseInsensitively()
    {
        var a = Dir("a"); var b = Dir("b");
        WriteAgent(a, "one.md", "helper");
        File.WriteAllText(Path.Combine(b, "two.md"), "---\nname: helper\ndescription: d\n---\nbody");

        var r = SubagentDefinitionLoader.LoadFromDirectories([(a, "project"), (b, "user")]);

        Assert.Single(r.Definitions);
    }

    [Fact]
    public void OnlyTopLevelMarkdown_ReadmeAndSubdirectoriesAndOtherExtensionsAreSkipped()
    {
        var d = Dir("agents");
        WriteAgent(d, "real.md", "real");
        WriteAgent(d, "README.md", "readme");
        File.WriteAllText(Path.Combine(d, "notes.txt"), "---\nname: txt\ndescription: d\n---\nbody");
        WriteAgent(Dir("agents/nested"), "deep.md", "deep");

        var r = SubagentDefinitionLoader.LoadFromDirectories([(d, "project")]);

        Assert.Equal(["real"], r.Definitions.Select(x => x.Name));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void OversizedFile_IsSkippedWithAReason()
    {
        var d = Dir("agents");
        WriteAgent(d, "huge.md", "huge", body: new string('x', 70 * 1024));
        WriteAgent(d, "ok.md", "ok");

        var r = SubagentDefinitionLoader.LoadFromDirectories([(d, "project")]);

        Assert.Equal(["ok"], r.Definitions.Select(x => x.Name));
        Assert.Contains(r.Problems, p => p.Contains("huge.md") && p.Contains("larger than"));
    }

    [Fact]
    public void MissingDirectories_AreNotAnError()
    {
        var r = SubagentDefinitionLoader.LoadFromDirectories([(Path.Combine(_root, "nope"), "project")]);

        Assert.Empty(r.Definitions);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void DefaultSearchDirs_ProjectBeforeUser_NativeBeforeShared_AndDeduplicated()
    {
        var dirs = SubagentDefinitionLoader.DefaultSearchDirs(_root);

        Assert.Equal(Path.Combine(_root, ".fuseraft", "agents"), dirs[0].Directory);
        Assert.Equal(Path.Combine(_root, ".agents", "agents"),   dirs[1].Directory);
        Assert.All(dirs.Take(2), d => Assert.Equal("project", d.Scope));
        Assert.All(dirs.Skip(2), d => Assert.Equal("user", d.Scope));
        Assert.Equal(dirs.Count, dirs.Select(d => d.Directory).Distinct().Count());
    }

    [Fact]
    public void DefaultSearchDirs_WhenCwdIsHome_DoesNotScanTheSameFolderTwice()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dirs = SubagentDefinitionLoader.DefaultSearchDirs(home);

        Assert.Equal(dirs.Count, dirs.Select(d => d.Directory).Distinct().Count());
        Assert.Equal("project", dirs.Single(d => d.Directory == Path.Combine(home, ".agents", "agents")).Scope);   // first occurrence wins
    }
}
