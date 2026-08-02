using fuseraft.Cli.Commands.Skills;

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
}
