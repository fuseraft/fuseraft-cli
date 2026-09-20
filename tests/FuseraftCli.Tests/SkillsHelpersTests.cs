using fuseraft.Cli.Commands.Skills;
using fuseraft.Core.Skills;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SkillsHelpers.CopySkillDirectory"/>, used by <c>fuseraft skills add</c>
/// to install a skill directory (SKILL.md plus any bundled references/scripts) into the global
/// skills library.
/// </summary>
public sealed class SkillsHelpersTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceDir;
    private readonly string _destDir;

    public SkillsHelpersTests()
    {
        _root      = Path.Combine(Path.GetTempPath(), "fuseraft_skills_helpers_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _sourceDir = Path.Combine(_root, "source");
        _destDir   = Path.Combine(_root, "dest");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CopySkillDirectory_CopiesSkillMd()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "---\nname: my-skill\n---\nbody");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.Equal("---\nname: my-skill\n---\nbody", File.ReadAllText(Path.Combine(_destDir, "SKILL.md")));
    }

    [Fact]
    public void CopySkillDirectory_CopiesReferencesSubdirectory()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        Directory.CreateDirectory(Path.Combine(_sourceDir, "references"));
        File.WriteAllText(Path.Combine(_sourceDir, "references", "guide.md"), "reference content");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        var copied = Path.Combine(_destDir, "references", "guide.md");
        Assert.True(File.Exists(copied));
        Assert.Equal("reference content", File.ReadAllText(copied));
    }

    [Fact]
    public void CopySkillDirectory_CopiesScriptsSubdirectory()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        Directory.CreateDirectory(Path.Combine(_sourceDir, "scripts"));
        File.WriteAllText(Path.Combine(_sourceDir, "scripts", "run.py"), "print('hi')");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.True(File.Exists(Path.Combine(_destDir, "scripts", "run.py")));
    }

    [Fact]
    public void CopySkillDirectory_NestedSubdirectories_PreservesStructure()
    {
        var nested = Path.Combine(_sourceDir, "references", "deep", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "file.md"), "deep content");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.Equal("deep content",
            File.ReadAllText(Path.Combine(_destDir, "references", "deep", "nested", "file.md")));
    }

    [Fact]
    public void CopySkillDirectory_ExistingDestFile_IsOverwritten()
    {
        Directory.CreateDirectory(_destDir);
        File.WriteAllText(Path.Combine(_destDir, "SKILL.md"), "old content");
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "new content");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.Equal("new content", File.ReadAllText(Path.Combine(_destDir, "SKILL.md")));
    }

    [Fact]
    public void CopySkillDirectory_CreatesDestDirectory_WhenMissing()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        Assert.False(Directory.Exists(_destDir));

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.True(Directory.Exists(_destDir));
    }

    [Fact]
    public void CopySkillDirectory_FileRemovedFromSource_IsPrunedFromDest()
    {
        // Simulate re-running `fuseraft skills add` after the skill author deleted a
        // bundled reference doc: the stale copy must not survive the update, or
        // read_skill_resource keeps serving content the source no longer has.
        Directory.CreateDirectory(Path.Combine(_destDir, "references"));
        File.WriteAllText(Path.Combine(_destDir, "SKILL.md"), "old body");
        File.WriteAllText(Path.Combine(_destDir, "references", "removed.md"), "stale content");

        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "new body");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.False(File.Exists(Path.Combine(_destDir, "references", "removed.md")));
        Assert.Equal("new body", File.ReadAllText(Path.Combine(_destDir, "SKILL.md")));
    }

    [Fact]
    public void CopySkillDirectory_FileStillInSource_SurvivesAlongsideRemovedOne()
    {
        Directory.CreateDirectory(Path.Combine(_destDir, "references"));
        File.WriteAllText(Path.Combine(_destDir, "SKILL.md"), "old body");
        File.WriteAllText(Path.Combine(_destDir, "references", "keep.md"), "old kept content");
        File.WriteAllText(Path.Combine(_destDir, "references", "removed.md"), "stale content");

        Directory.CreateDirectory(Path.Combine(_sourceDir, "references"));
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "new body");
        File.WriteAllText(Path.Combine(_sourceDir, "references", "keep.md"), "new kept content");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.False(File.Exists(Path.Combine(_destDir, "references", "removed.md")));
        Assert.Equal("new kept content", File.ReadAllText(Path.Combine(_destDir, "references", "keep.md")));
    }

    [Fact]
    public void CopySkillDirectory_GitDirectory_IsNotCopied()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        Directory.CreateDirectory(Path.Combine(_sourceDir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_sourceDir, ".git", "HEAD"), "ref: refs/heads/main");
        File.WriteAllText(Path.Combine(_sourceDir, ".git", "objects", "pack"), "blob");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.True(File.Exists(Path.Combine(_destDir, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(_destDir, ".git")));
    }

    [Fact]
    public void CopySkillDirectory_GitPointerFile_IsNotCopied()
    {
        // Worktrees and submodules have a .git *file* ("gitdir: ...") instead of a directory.
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        File.WriteAllText(Path.Combine(_sourceDir, ".git"), "gitdir: /elsewhere/.git/worktrees/x");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.False(File.Exists(Path.Combine(_destDir, ".git")));
    }

    [Fact]
    public void CopySkillDirectory_NestedGitDirectory_IsNotCopied()
    {
        Directory.CreateDirectory(Path.Combine(_sourceDir, "references", "vendor", ".git"));
        File.WriteAllText(Path.Combine(_sourceDir, "references", "vendor", ".git", "HEAD"), "ref");
        File.WriteAllText(Path.Combine(_sourceDir, "references", "vendor", "doc.md"), "vendored doc");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.True(File.Exists(Path.Combine(_destDir, "references", "vendor", "doc.md")));
        Assert.False(Directory.Exists(Path.Combine(_destDir, "references", "vendor", ".git")));
    }

    [Fact]
    public void CopySkillDirectory_GitIgnoreAndSimilarlyNamedFiles_AreStillCopied()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");
        File.WriteAllText(Path.Combine(_sourceDir, ".gitignore"), "bin/");
        File.WriteAllText(Path.Combine(_sourceDir, ".gitattributes"), "* text=auto");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.True(File.Exists(Path.Combine(_destDir, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(_destDir, ".gitattributes")));
    }

    [Fact]
    public void CopySkillDirectory_StaleGitDirectoryInDest_IsPruned()
    {
        // A skill installed before .git was excluded must lose its copy on the next `skills add`.
        Directory.CreateDirectory(Path.Combine(_destDir, ".git"));
        File.WriteAllText(Path.Combine(_destDir, ".git", "HEAD"), "ref");
        File.WriteAllText(Path.Combine(_sourceDir, "SKILL.md"), "body");

        SkillsHelpers.CopySkillDirectory(_sourceDir, _destDir);

        Assert.False(File.Exists(Path.Combine(_destDir, ".git", "HEAD")));
    }

    // ── ExtractSlug / ExtractDescription / CanonicalizeName ────────────────────

    [Fact]
    public void ExtractSlug_SlugifiesRawName()
    {
        var content = "---\nname: My Bad Skill!!\ndescription: A skill.\n---";
        Assert.Equal("my-bad-skill", SkillsHelpers.ExtractSlug(content));
    }

    [Fact]
    public void ExtractSlug_NoNameField_ReturnsNull()
    {
        Assert.Null(SkillsHelpers.ExtractSlug("---\ndescription: A skill.\n---"));
    }

    [Fact]
    public void CanonicalizeName_NameAlreadyMatchesSlug_ReturnsContentUnchanged()
    {
        const string content = "---\nname: my-skill\ndescription: A skill.\n---\n\nBody";
        Assert.Same(content, SkillsHelpers.CanonicalizeName(content, "my-skill"));
    }

    [Fact]
    public void CanonicalizeName_NameDiffersFromSlug_RewritesNameField()
    {
        var content   = "---\nname: My Bad Skill!!\ndescription: A skill.\n---\n\nBody";
        var rewritten = SkillsHelpers.CanonicalizeName(content, "my-bad-skill");

        Assert.Equal("my-bad-skill", SkillsHelpers.ExtractSlug(rewritten));
        Assert.Equal("A skill.", FrontmatterFieldReader.ExtractField(rewritten, "description"));
        Assert.Contains("Body", rewritten);
    }
}
