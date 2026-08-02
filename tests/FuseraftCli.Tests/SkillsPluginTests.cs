using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SkillsPlugin"/>.
///
/// Each test gets an isolated temp directory. All slug-to-dir entries in the plugin
/// point into that directory so no real skill library is touched.
/// </summary>
public sealed class SkillsPluginTests : IDisposable
{
    private readonly string _root;

    public SkillsPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_skills_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── helpers ──────────────────────────────────────────────────────────────

    private string MakeSkillDir(string slug, string? content = null)
    {
        var dir = Path.Combine(_root, slug);
        Directory.CreateDirectory(dir);
        if (content is not null)
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        return dir;
    }

    private SkillsPlugin PluginFor(params (string Slug, string? Content)[] skills)
    {
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slug, content) in skills)
            dirs[slug] = MakeSkillDir(slug, content);
        return new SkillsPlugin(dirs);
    }

    // ── LoadSkillAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadSkill_UnknownSlug_ReturnsNotFound()
    {
        var plugin = PluginFor(("my-skill", "content"));
        var result = await plugin.LoadSkillAsync("does-not-exist");
        Assert.StartsWith("[NOT FOUND]", result);
        Assert.Contains("does-not-exist", result);
    }

    [Fact]
    public async Task LoadSkill_UnknownSlug_ListsKnownSkillsInMessage()
    {
        var plugin = PluginFor(("alpha", "body"), ("beta", "body"));
        var result = await plugin.LoadSkillAsync("gamma");
        Assert.Contains("alpha", result);
        Assert.Contains("beta", result);
    }

    [Fact]
    public async Task LoadSkill_ValidSlug_ReturnsFileContent()
    {
        const string body = "## Do the thing\n1. Step one\n2. Step two";
        var plugin = PluginFor(("my-skill", body));
        var result = await plugin.LoadSkillAsync("my-skill");
        Assert.Equal(body, result);
    }

    [Fact]
    public async Task LoadSkill_EmptySkillFile_ReturnsEmptyString()
    {
        var plugin = PluginFor(("empty-skill", ""));
        var result = await plugin.LoadSkillAsync("empty-skill");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task LoadSkill_SkillMdDeletedAfterInit_ReturnsError()
    {
        // TOCTOU: file disappears between plugin construction and the load call.
        var dir = MakeSkillDir("vanishing", "some content");
        File.Delete(Path.Combine(dir, "SKILL.md"));

        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["vanishing"] = dir });
        var result = await plugin.LoadSkillAsync("vanishing");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task LoadSkill_SlugIsCaseInsensitive()
    {
        var plugin = PluginFor(("My-Skill", "body"));
        var result = await plugin.LoadSkillAsync("my-skill");
        Assert.Equal("body", result);
    }

    [Fact]
    public async Task LoadSkill_DoesNotThrow_ReturnsStringResult()
    {
        // Any slug → result must be a string, never an unhandled exception.
        var plugin = new SkillsPlugin(new Dictionary<string, string>());
        var ex     = await Record.ExceptionAsync(() => plugin.LoadSkillAsync("anything"));
        Assert.Null(ex);
    }

    // ── ReadSkillResourceAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ReadSkillResource_UnknownSkill_ReturnsNotFound()
    {
        var plugin = PluginFor(("real-skill", "body"));
        var result = await plugin.ReadSkillResourceAsync("ghost", "references/x.md");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task ReadSkillResource_EmptyPath_ReturnsError()
    {
        var plugin = PluginFor(("my-skill", "body"));
        var result = await plugin.ReadSkillResourceAsync("my-skill", "");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task ReadSkillResource_MissingFile_ReturnsNotFound()
    {
        var plugin = PluginFor(("my-skill", "body"));
        var result = await plugin.ReadSkillResourceAsync("my-skill", "references/missing.md");
        Assert.StartsWith("[NOT FOUND]", result);
        Assert.Contains("references/missing.md", result);
    }

    [Fact]
    public async Task ReadSkillResource_NestedFile_ReturnsContent()
    {
        var dir = MakeSkillDir("my-skill", "body");
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        File.WriteAllText(Path.Combine(dir, "references", "style-guide.md"), "# Style Guide\nUse tabs.");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.ReadSkillResourceAsync("my-skill", "references/style-guide.md");
        Assert.Equal("# Style Guide\nUse tabs.", result);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("references/../../secret.txt")]
    public async Task ReadSkillResource_PathTraversal_ReturnsError(string traversalPath)
    {
        var dir = MakeSkillDir("my-skill", "body");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "top secret");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.ReadSkillResourceAsync("my-skill", traversalPath);
        Assert.StartsWith("[ERROR]", result);
        Assert.DoesNotContain("top secret", result);
    }

    [Fact]
    public async Task ReadSkillResource_AbsolutePathEscape_ReturnsError()
    {
        var dir = MakeSkillDir("my-skill", "body");
        var outsideFile = Path.Combine(_root, "secret.txt");
        File.WriteAllText(outsideFile, "top secret");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.ReadSkillResourceAsync("my-skill", outsideFile);
        Assert.StartsWith("[ERROR]", result);
        Assert.DoesNotContain("top secret", result);
    }

    // ── RunSkillScriptAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RunSkillScript_UnknownSkill_ReturnsNotFound()
    {
        var plugin = PluginFor(("real-skill", "body"));
        var result = await plugin.RunSkillScriptAsync("ghost", "run.sh");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task RunSkillScript_PathTraversal_ReturnsError()
    {
        var dir = MakeSkillDir("my-skill", "body");
        var outsideScript = Path.Combine(_root, "evil.sh");
        File.WriteAllText(outsideScript, "#!/bin/sh\necho pwned\n");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.RunSkillScriptAsync("my-skill", "../evil.sh");
        Assert.StartsWith("[ERROR]", result);
        Assert.DoesNotContain("pwned", result);
    }

    [Fact]
    public async Task RunSkillScript_NestedScriptPath_Runs()
    {
        var dir = MakeSkillDir("my-skill", "body");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        File.WriteAllText(Path.Combine(dir, "scripts", "hello.sh"), "#!/bin/sh\necho nested-ok\n");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.RunSkillScriptAsync("my-skill", "scripts/hello.sh");
        Assert.Contains("nested-ok", result);
    }

    [Fact]
    public async Task RunSkillScript_ScriptFileMissing_ReturnsNotFound()
    {
        var plugin = PluginFor(("my-skill", "body"));
        var result = await plugin.RunSkillScriptAsync("my-skill", "missing.sh");
        Assert.StartsWith("[NOT FOUND]", result);
        Assert.Contains("missing.sh", result);
    }

    [Fact]
    public async Task RunSkillScript_UnsupportedExtension_ReturnsError()
    {
        var dir = MakeSkillDir("my-skill", "body");
        File.WriteAllText(Path.Combine(dir, "run.exe"), "binary");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.RunSkillScriptAsync("my-skill", "run.exe");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains(".exe", result);
    }

    [Fact]
    public async Task RunSkillScript_ShellScript_ReturnsStdout()
    {
        var dir = MakeSkillDir("my-skill", "body");
        File.WriteAllText(Path.Combine(dir, "hello.sh"), "#!/bin/sh\necho hello-from-skill\n");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.RunSkillScriptAsync("my-skill", "hello.sh");
        Assert.Contains("hello-from-skill", result);
    }

    [Fact]
    public async Task RunSkillScript_ScriptWritesToStderr_StderrAppendedToResult()
    {
        var dir = MakeSkillDir("my-skill", "body");
        File.WriteAllText(Path.Combine(dir, "warn.sh"), "#!/bin/sh\necho out\necho err >&2\n");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var result = await plugin.RunSkillScriptAsync("my-skill", "warn.sh");
        Assert.Contains("out",    result);
        Assert.Contains("stderr", result);
        Assert.Contains("err",    result);
    }

    [Fact]
    public async Task RunSkillScript_EmptyArgs_DoesNotThrow()
    {
        var dir = MakeSkillDir("my-skill", "body");
        File.WriteAllText(Path.Combine(dir, "noop.sh"), "#!/bin/sh\necho ok\n");
        var plugin = new SkillsPlugin(new Dictionary<string, string> { ["my-skill"] = dir });

        var ex = await Record.ExceptionAsync(() => plugin.RunSkillScriptAsync("my-skill", "noop.sh", args: ""));
        Assert.Null(ex);
    }
}
