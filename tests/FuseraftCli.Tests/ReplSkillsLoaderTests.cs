using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReplSkillsLoader"/>: the thin REPL-side wiring over
/// Microsoft.Agents.AI's <c>AgentFileSkillsSource</c>/<c>AgentSkillsProvider</c>.
///
/// <para>
/// These tests deliberately do not re-verify frontmatter validation rules (kebab-case format,
/// length limits, name-matches-directory, ...) — that's Microsoft's own, separately-tested
/// behavior. What's fuseraft-specific and worth covering here is the wiring itself: that
/// discovery results, catalog instructions, and tools all come back consistently, and that
/// dedup/precedence across multiple search directories works as the REPL depends on.
/// </para>
/// </summary>
public sealed class ReplSkillsLoaderTests : IDisposable
{
    private readonly string _root;
    private static readonly IChatClient StubClient = new NonInvocableStubChatClient();

    public ReplSkillsLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_repl_loader_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteSkill(string relativeDir, string name, string description, string body = "## Steps\n1. Do it.")
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: \"{description}\"\n---\n\n{body}");
        return dir;
    }

    private static Task<ReplSkillsResult> Build(params string[] searchDirs) =>
        ReplSkillsLoader.BuildAsync(StubClient, NullLoggerFactory.Instance, searchDirs, CancellationToken.None);

    [Fact]
    public async Task BuildAsync_NoSearchDirs_ReturnsEmpty()
    {
        var result = await Build();

        Assert.Empty(result.Skills);
        Assert.Null(result.CatalogInstructions);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task BuildAsync_SearchDirDoesNotExist_ReturnsEmpty()
    {
        var result = await Build(Path.Combine(_root, "nonexistent"));

        Assert.Empty(result.Skills);
        Assert.Null(result.CatalogInstructions);
    }

    [Fact]
    public async Task BuildAsync_OneValidSkill_ReturnsSkillCatalogAndTools()
    {
        WriteSkill("fetch-api", "fetch-api", "Use when fetching REST data.");

        var result = await Build(_root);

        Assert.Single(result.Skills);
        Assert.Equal("fetch-api", result.Skills[0].Frontmatter.Name);
        Assert.NotNull(result.CatalogInstructions);
        Assert.Contains("fetch-api", result.CatalogInstructions);
        Assert.Contains("Use when fetching REST data.", result.CatalogInstructions);
    }

    [Fact]
    public async Task BuildAsync_ValidSkill_ExposesLoadReadRunSkillTools()
    {
        WriteSkill("my-skill", "my-skill", "A skill.");

        var result = await Build(_root);

        var toolNames = result.Tools.Select(t => t.Name).ToList();
        Assert.Contains("load_skill", toolNames);
        Assert.Contains("read_skill_resource", toolNames);
        Assert.Contains("run_skill_script", toolNames);
    }

    [Fact]
    public async Task BuildAsync_LoadSkillTool_ReturnsFullContent()
    {
        WriteSkill("my-skill", "my-skill", "A skill.", body: "## Do the thing\nStep one.");

        var result = await Build(_root);
        var loadSkill = result.Tools.Single(t => t.Name == "load_skill");

        var content = await loadSkill.InvokeAsync(new AIFunctionArguments { ["skillName"] = "my-skill" });

        Assert.Contains("Do the thing", content?.ToString());
    }

    [Fact]
    public async Task BuildAsync_NameDoesNotMatchDirectory_SkillIsSilentlyExcluded()
    {
        // AgentFileSkillsSource's own validation, not fuseraft's — covered here only to confirm
        // the wiring surfaces that behavior rather than working around it.
        WriteSkill("mismatched-dir", "totally-different-name", "A description.");

        var result = await Build(_root);

        Assert.Empty(result.Skills);
        Assert.Null(result.CatalogInstructions);
    }

    [Fact]
    public async Task BuildAsync_MultipleValidSkills_AllDiscovered()
    {
        WriteSkill("alpha", "alpha", "First skill.");
        WriteSkill("beta",  "beta",  "Second skill.");

        var result = await Build(_root);

        Assert.Equal(2, result.Skills.Count);
        Assert.Contains(result.Skills, s => s.Frontmatter.Name == "alpha");
        Assert.Contains(result.Skills, s => s.Frontmatter.Name == "beta");
    }

    [Fact]
    public async Task BuildAsync_DuplicateSlugAcrossSearchDirs_DeduplicatedAndFirstDirWins()
    {
        var dir1 = Path.Combine(_root, "priority1");
        var dir2 = Path.Combine(_root, "priority2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var skill1 = Path.Combine(dir1, "my-skill");
        var skill2 = Path.Combine(dir2, "my-skill");
        Directory.CreateDirectory(skill1);
        Directory.CreateDirectory(skill2);
        File.WriteAllText(Path.Combine(skill1, "SKILL.md"), "---\nname: my-skill\ndescription: \"From dir1\"\n---");
        File.WriteAllText(Path.Combine(skill2, "SKILL.md"), "---\nname: my-skill\ndescription: \"From dir2\"\n---");

        var result = await Build(dir1, dir2);

        // The banner count (result.Skills) must agree with what the catalog actually advertises —
        // both must be deduplicated by name, not just the catalog.
        Assert.Single(result.Skills);
        Assert.Equal("From dir1", result.Skills[0].Frontmatter.Description);
        Assert.Contains("From dir1", result.CatalogInstructions!);
        Assert.DoesNotContain("From dir2", result.CatalogInstructions!);
    }

    [Fact]
    public async Task BuildAsync_DirHasNoSkillMdFiles_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "not a skill");

        var result = await Build(_root);

        Assert.Empty(result.Skills);
    }

    private sealed class NonInvocableStubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Skill discovery should never actually invoke the chat client.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Skill discovery should never actually invoke the chat client.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
