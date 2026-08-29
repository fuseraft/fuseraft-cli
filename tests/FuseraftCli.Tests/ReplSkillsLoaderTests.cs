using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReplSkillsLoader"/>: directory scanning, frontmatter parsing,
/// catalog generation, and resilience to bad/missing inputs.
///
/// All tests use an isolated temp directory; no real skill library is touched.
/// </summary>
public sealed class ReplSkillsLoaderTests : IDisposable
{
    private readonly string _root;

    public ReplSkillsLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_loader_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a skill dir with a SKILL.md at _root/slug/SKILL.md.</summary>
    private string WriteSkill(string slug, string content)
    {
        var dir = Path.Combine(_root, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        return dir;
    }

    private string ValidSkillMd(string name, string description, string body = "## Steps\n1. Do it.")
        => $"---\nname: {name}\ndescription: \"{description}\"\n---\n\n{body}";

    // ── ParseSkillDescription ─────────────────────────────────────────────────

    [Fact]
    public void ParseSkillDescription_WellFormedFrontmatter_ReturnsDescription()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\nname: my-skill\ndescription: \"Use when doing X.\"\n---\n\n# Body");

        var desc = ReplSkillsLoader.ParseSkillDescription(path);
        Assert.Equal("Use when doing X.", desc);
    }

    [Fact]
    public void ParseSkillDescription_SingleQuotedValue_ReturnsUnquotedDescription()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\ndescription: 'Use when doing Y.'\n---");

        var desc = ReplSkillsLoader.ParseSkillDescription(path);
        Assert.Equal("Use when doing Y.", desc);
    }

    [Fact]
    public void ParseSkillDescription_UnquotedValue_ReturnsTrimmedDescription()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\ndescription: Use when doing Z.\n---");

        var desc = ReplSkillsLoader.ParseSkillDescription(path);
        Assert.Equal("Use when doing Z.", desc);
    }

    [Fact]
    public void ParseSkillDescription_DescriptionWithColons_ReturnsFullValue()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\ndescription: \"Use when: A, B, or C.\"\n---");

        var desc = ReplSkillsLoader.ParseSkillDescription(path);
        Assert.Equal("Use when: A, B, or C.", desc);
    }

    [Fact]
    public void ParseSkillDescription_EmptyValue_ReturnsNull()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\ndescription: \"\"\n---");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_WhitespaceOnlyValue_ReturnsNull()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\ndescription:    \n---");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_NoDescriptionField_ReturnsNull()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\nname: my-skill\n---\n\n# Body");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_NoFrontmatter_ReturnsNull()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "# No frontmatter here\n\nJust body text.");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_UnclosedFrontmatter_ReturnsNull()
    {
        // Opening --- but no closing ---; reads to EOF without finding the field.
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "---\nname: skill\n\nNo closing delimiter");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_EmptyFile_ReturnsNull()
    {
        var path = Path.Combine(_root, "skill.md");
        File.WriteAllText(path, "");

        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    [Fact]
    public void ParseSkillDescription_FileDoesNotExist_ReturnsNull()
    {
        var path = Path.Combine(_root, "nonexistent.md");
        Assert.Null(ReplSkillsLoader.ParseSkillDescription(path));
    }

    // ── BuildSkills — no skills ───────────────────────────────────────────────

    [Fact]
    public void BuildSkills_NoSearchDirs_ReturnsNull()
    {
        var (plugin, catalog) = ReplSkillsLoader.BuildSkills(Array.Empty<string>());
        Assert.Null(plugin);
        Assert.Null(catalog);
    }

    [Fact]
    public void BuildSkills_SearchDirDoesNotExist_ReturnsNull()
    {
        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([Path.Combine(_root, "nonexistent")]);
        Assert.Null(plugin);
        Assert.Null(catalog);
    }

    [Fact]
    public void BuildSkills_SearchDirExistsButEmpty_ReturnsNull()
    {
        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([_root]);
        Assert.Null(plugin);
        Assert.Null(catalog);
    }

    [Fact]
    public void BuildSkills_DirHasNoSkillMdFiles_ReturnsNull()
    {
        // A file called something else — should be ignored.
        File.WriteAllText(Path.Combine(_root, "README.md"), "not a skill");
        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([_root]);
        Assert.Null(plugin);
        Assert.Null(catalog);
    }

    // ── BuildSkills — valid skills ────────────────────────────────────────────

    [Fact]
    public void BuildSkills_OneValidSkill_ReturnsPluginAndCatalog()
    {
        WriteSkill("fetch-api", ValidSkillMd("fetch-api", "Use when fetching REST data."));

        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.NotNull(plugin);
        Assert.NotNull(catalog);
        Assert.Equal(1, plugin!.Count);
    }

    [Fact]
    public void BuildSkills_CatalogContainsSlugAndDescription()
    {
        WriteSkill("fetch-api", ValidSkillMd("fetch-api", "Use when fetching REST data."));

        var (_, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.Contains("fetch-api",                   catalog!);
        Assert.Contains("Use when fetching REST data.", catalog);
    }

    [Fact]
    public void BuildSkills_CatalogContainsLoadSkillInstruction()
    {
        WriteSkill("my-skill", ValidSkillMd("my-skill", "A skill."));

        var (_, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.Contains("load_skill", catalog!);
    }

    [Fact]
    public void BuildSkills_MultipleSkills_AllAppearInCatalog()
    {
        WriteSkill("alpha", ValidSkillMd("alpha", "First skill."));
        WriteSkill("beta",  ValidSkillMd("beta",  "Second skill."));

        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.Equal(2, plugin!.Count);
        Assert.Contains("alpha", catalog!);
        Assert.Contains("beta",  catalog);
    }

    [Fact]
    public void BuildSkills_CatalogSlugsAreSorted()
    {
        WriteSkill("zebra", ValidSkillMd("zebra", "Z skill."));
        WriteSkill("alpha", ValidSkillMd("alpha", "A skill."));

        var (_, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        var alphaPos = catalog!.IndexOf("alpha", StringComparison.Ordinal);
        var zebraPos = catalog.IndexOf("zebra", StringComparison.Ordinal);
        Assert.True(alphaPos < zebraPos, "Catalog should list skills in alphabetical order");
    }

    [Fact]
    public void BuildSkills_SkillWithNoDescription_SlugAppearsWithoutTrailingColon()
    {
        // Skill with no description field — just a bare slug in the catalog.
        WriteSkill("bare-skill", "# No frontmatter here");

        var (plugin, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.NotNull(plugin);
        Assert.Contains("bare-skill", catalog!);
        Assert.DoesNotContain("bare-skill:", catalog); // no trailing colon
    }

    [Fact]
    public void BuildSkills_SkillWithEmptyDescription_SlugAppearsWithoutTrailingColon()
    {
        WriteSkill("empty-desc", "---\ndescription: \"\"\n---\n# Body");

        var (_, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.DoesNotContain("empty-desc:", catalog!);
    }

    // ── BuildSkills — priority and deduplication ──────────────────────────────

    [Fact]
    public void BuildSkills_DuplicateSlugAcrossDirs_FirstDirWins()
    {
        var dir1 = Path.Combine(_root, "priority1");
        var dir2 = Path.Combine(_root, "priority2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var skill1 = Path.Combine(dir1, "my-skill");
        var skill2 = Path.Combine(dir2, "my-skill");
        Directory.CreateDirectory(skill1);
        Directory.CreateDirectory(skill2);
        File.WriteAllText(Path.Combine(skill1, "SKILL.md"), "---\ndescription: \"From dir1\"\n---");
        File.WriteAllText(Path.Combine(skill2, "SKILL.md"), "---\ndescription: \"From dir2\"\n---");

        var (_, catalog) = ReplSkillsLoader.BuildSkills([dir1, dir2]);

        Assert.Contains("From dir1", catalog!);
        Assert.DoesNotContain("From dir2", catalog);
    }

    // ── BuildSkills — resilience ──────────────────────────────────────────────

    [Fact]
    public void BuildSkills_SkillMdWithGarbageContent_DoesNotThrow()
    {
        // Completely invalid content — should be indexed with a null description.
        WriteSkill("garbage", "\x00\x01\x02 not UTF-8 friendly binary content \xff\xfe");

        var ex = Record.Exception(() => ReplSkillsLoader.BuildSkills([_root]));
        Assert.Null(ex);
    }

    [Fact]
    public void BuildSkills_MixOfValidAndInvalidSkills_ValidOnesStillLoaded()
    {
        WriteSkill("good",    ValidSkillMd("good",    "A well-formed skill."));
        WriteSkill("badfile", "---\n: invalid yaml :\n---");

        var (plugin, _) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.NotNull(plugin);
        Assert.Equal(2, plugin!.Count); // both dirs indexed; bad frontmatter just gives null desc
    }

    [Fact]
    public void BuildSkills_EmptySkillMd_DoesNotThrow()
    {
        WriteSkill("empty", "");

        var ex = Record.Exception(() => ReplSkillsLoader.BuildSkills([_root]));
        Assert.Null(ex);
    }

    [Fact]
    public void BuildSkills_SkillMdIsDirectory_DoesNotThrow()
    {
        // Edge case: a path called "SKILL.md" that is actually a directory.
        var slugDir  = Path.Combine(_root, "weird-skill");
        var fakeMd   = Path.Combine(slugDir, "SKILL.md");
        Directory.CreateDirectory(fakeMd); // SKILL.md is a directory, not a file

        var ex = Record.Exception(() => ReplSkillsLoader.BuildSkills([_root]));
        Assert.Null(ex);
    }

    // ── BuildSkillsDetailed — spec conformance (parity with orchestration) ────

    [Fact]
    public void BuildSkillsDetailed_NameDoesNotMatchDirectory_SkipsWithWarning()
    {
        WriteSkill("mismatched-dir", ValidSkillMd("totally-different-name", "A description."));

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.Null(result.Plugin);
        Assert.Contains(result.Warnings, w => w.Contains("does not match") && w.Contains("mismatched-dir"));
    }

    [Fact]
    public void BuildSkillsDetailed_DeclaredNameInvalidFormat_SkipsWithWarning()
    {
        WriteSkill("Bad-Name", ValidSkillMd("Bad-Name", "A description.")); // uppercase not allowed

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.Null(result.Plugin);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void BuildSkillsDetailed_DeclaredDescriptionTooLong_SkipsWithWarning()
    {
        var longDescription = new string('a', 1025);
        WriteSkill("my-skill", ValidSkillMd("my-skill", longDescription));

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.Null(result.Plugin);
        Assert.Contains(result.Warnings, w => w.Contains("1024"));
    }

    [Fact]
    public void BuildSkillsDetailed_NoNameFieldAtAll_LoadsLeniently_NoWarning()
    {
        // Directory-name-only skills (no 'name:' field) remain a supported, warning-free
        // REPL convenience even though orchestration requires a declared, matching name.
        WriteSkill("my-skill", "---\ndescription: \"A description.\"\n---\n\nBody.");

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.NotNull(result.Plugin);
        Assert.Equal(1, result.Plugin!.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BuildSkillsDetailed_ValidNameAndDirectoryMatch_NoWarning()
    {
        WriteSkill("my-skill", ValidSkillMd("my-skill", "A description."));

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.NotNull(result.Plugin);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BuildSkills_CompatibilityField_AppearsInCatalog()
    {
        var dir = Path.Combine(_root, "my-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            "---\nname: my-skill\ndescription: \"A description.\"\ncompatibility: \"Requires docker\"\n---\n\nBody.");

        var (_, catalog) = ReplSkillsLoader.BuildSkills([_root]);

        Assert.Contains("Requires docker", catalog!);
    }

    [Fact]
    public void BuildSkillsDetailed_DeclaredCompatibilityTooLong_SkipsWithWarning()
    {
        var dir = Path.Combine(_root, "my-skill");
        Directory.CreateDirectory(dir);
        var longCompat = new string('a', 501);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: my-skill\ndescription: \"A description.\"\ncompatibility: \"{longCompat}\"\n---\n\nBody.");

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.Null(result.Plugin);
        Assert.Contains(result.Warnings, w => w.Contains("500"));
    }

    [Fact]
    public void BuildSkillsDetailed_NestedVendorNamespace_TwoLevelsDeep_IsDiscovered()
    {
        // Matches orchestration's AgentFileSkillsSource search depth (root/vendor/skill/SKILL.md).
        WriteSkill(Path.Combine("vendor", "my-skill"), ValidSkillMd("my-skill", "A description."));

        var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

        Assert.NotNull(result.Plugin);
        Assert.True(result.Plugin!.HasSkill("my-skill"));
    }

    [Fact]
    public void BuildSkillsDetailed_SymlinkedSkillDirectory_IsNotFollowed()
    {
        var realSkillRoot = Path.Combine(Path.GetTempPath(), "fuseraft_loader_tests_real_" + Guid.NewGuid().ToString("N")[..8]);
        var realSkillDir  = Path.Combine(realSkillRoot, "real-skill");
        Directory.CreateDirectory(realSkillDir);
        File.WriteAllText(Path.Combine(realSkillDir, "SKILL.md"), ValidSkillMd("real-skill", "A description."));

        try
        {
            var link = Path.Combine(_root, "linked-skill");
            try
            {
                Directory.CreateSymbolicLink(link, realSkillDir);
            }
            catch (Exception)
            {
                return; // environment doesn't allow symlinks — skip
            }

            var result = ReplSkillsLoader.BuildSkillsDetailed([_root]);

            Assert.Null(result.Plugin); // the only skill lives behind a symlink, which must not be followed
        }
        finally
        {
            Directory.Delete(realSkillRoot, recursive: true);
        }
    }
}
