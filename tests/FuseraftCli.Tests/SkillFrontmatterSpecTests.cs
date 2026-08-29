using fuseraft.Core.Skills;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SkillFrontmatterSpec"/> — the single parser/validator shared by the
/// REPL loader, orchestration-visible <c>skills add</c>/<c>skills validate</c> commands, and
/// skill curation, so all surfaces agree on what a spec-conformant SKILL.md looks like.
/// </summary>
public sealed class SkillFrontmatterSpecTests
{
    // ── TryParse ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_NoFrontmatter_ReturnsNull()
    {
        Assert.Null(SkillFrontmatterSpec.TryParse("# Just a heading\n\nSome body text."));
    }

    [Fact]
    public void TryParse_EmptyContent_ReturnsNull()
    {
        Assert.Null(SkillFrontmatterSpec.TryParse(""));
        Assert.Null(SkillFrontmatterSpec.TryParse(null));
    }

    [Fact]
    public void TryParse_UnclosedFrontmatter_ReturnsNull()
    {
        Assert.Null(SkillFrontmatterSpec.TryParse("---\nname: skill\n\nNo closing delimiter"));
    }

    [Fact]
    public void TryParse_MinimalValidFrontmatter_ExtractsNameAndDescription()
    {
        var fm = SkillFrontmatterSpec.TryParse("---\nname: pdf-processing\ndescription: Extract PDF text.\n---\n\nBody");
        Assert.NotNull(fm);
        Assert.Equal("pdf-processing", fm!.Name);
        Assert.Equal("Extract PDF text.", fm.Description);
    }

    [Fact]
    public void TryParse_DoubleQuotedDescriptionWithColon_PreservesColon()
    {
        var fm = SkillFrontmatterSpec.TryParse("---\ndescription: \"Use when: A, B, or C.\"\n---");
        Assert.Equal("Use when: A, B, or C.", fm!.Description);
    }

    [Fact]
    public void TryParse_SingleQuotedValue_Unquotes()
    {
        var fm = SkillFrontmatterSpec.TryParse("---\ndescription: 'Use when doing Y.'\n---");
        Assert.Equal("Use when doing Y.", fm!.Description);
    }

    [Fact]
    public void TryParse_OptionalFields_AllExtracted()
    {
        var content = """
            ---
            name: pdf-processing
            description: Extract PDF text, fill forms, merge files.
            license: Apache-2.0
            compatibility: Requires Python 3.14+ and uv
            allowed-tools: Bash(git:*) Bash(jq:*) Read
            metadata:
              author: example-org
              version: "1.0"
            ---

            Body.
            """;

        var fm = SkillFrontmatterSpec.TryParse(content);

        Assert.NotNull(fm);
        Assert.Equal("Apache-2.0", fm!.License);
        Assert.Equal("Requires Python 3.14+ and uv", fm.Compatibility);
        Assert.Equal("Bash(git:*) Bash(jq:*) Read", fm.AllowedTools);
        Assert.NotNull(fm.Metadata);
        Assert.Equal("example-org", fm.Metadata!["author"]);
        Assert.Equal("1.0", fm.Metadata["version"]);
    }

    [Fact]
    public void TryParse_MetadataKeysDoNotLeakIntoTopLevelFields()
    {
        // A "name:" or "description:" line indented under metadata: must not be picked up
        // as the top-level field — only unindented lines are top-level.
        var content = """
            ---
            name: real-skill
            description: Real description.
            metadata:
              name: this-is-just-metadata
            ---
            """;

        var fm = SkillFrontmatterSpec.TryParse(content);

        Assert.Equal("real-skill", fm!.Name);
        Assert.Equal("this-is-just-metadata", fm.Metadata!["name"]);
    }

    [Fact]
    public void TryParse_UnrecognizedTopLevelKeys_AreIgnoredWithoutError()
    {
        // Third-party skills sometimes add non-spec fields (e.g. "version:", "homepage:").
        var content = "---\nname: my-skill\ndescription: A skill.\nversion: 1.5.2\nhomepage: https://example.com\n---";
        var fm = SkillFrontmatterSpec.TryParse(content);

        Assert.Equal("my-skill", fm!.Name);
        Assert.Equal("A skill.", fm.Description);
    }

    [Fact]
    public void TryParse_NoRecognizedFields_ReturnsNull()
    {
        Assert.Null(SkillFrontmatterSpec.TryParse("---\n: invalid yaml :\n---"));
    }

    // ── ValidateName ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pdf-processing")]
    [InlineData("data-analysis")]
    [InlineData("a")]
    [InlineData("a1-b2")]
    public void ValidateName_ValidNames_Pass(string name)
    {
        Assert.True(SkillFrontmatterSpec.ValidateName(name, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ValidateName_Null_Fails()
    {
        Assert.False(SkillFrontmatterSpec.ValidateName(null, out var reason));
        Assert.Contains("required", reason);
    }

    [Fact]
    public void ValidateName_TooLong_Fails()
    {
        var name = new string('a', 65);
        Assert.False(SkillFrontmatterSpec.ValidateName(name, out var reason));
        Assert.Contains("64", reason);
    }

    [Theory]
    [InlineData("PDF-Processing")]
    [InlineData("-pdf")]
    [InlineData("pdf-")]
    [InlineData("pdf--processing")]
    [InlineData("pdf_processing")]
    [InlineData("pdf processing")]
    public void ValidateName_InvalidFormats_Fail(string name)
    {
        Assert.False(SkillFrontmatterSpec.ValidateName(name, out var reason));
        Assert.NotNull(reason);
    }

    // ── ValidateDescription ──────────────────────────────────────────────────

    [Fact]
    public void ValidateDescription_Empty_Fails()
    {
        Assert.False(SkillFrontmatterSpec.ValidateDescription("", out var reason));
        Assert.Contains("required", reason);
    }

    [Fact]
    public void ValidateDescription_TooLong_Fails()
    {
        var desc = new string('a', 1025);
        Assert.False(SkillFrontmatterSpec.ValidateDescription(desc, out var reason));
        Assert.Contains("1024", reason);
    }

    [Fact]
    public void ValidateDescription_ExactlyMaxLength_Passes()
    {
        var desc = new string('a', 1024);
        Assert.True(SkillFrontmatterSpec.ValidateDescription(desc, out _));
    }

    // ── ValidateCompatibility ────────────────────────────────────────────────

    [Fact]
    public void ValidateCompatibility_Null_Passes()
    {
        Assert.True(SkillFrontmatterSpec.ValidateCompatibility(null, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ValidateCompatibility_TooLong_Fails()
    {
        Assert.False(SkillFrontmatterSpec.ValidateCompatibility(new string('a', 501), out var reason));
        Assert.Contains("500", reason);
    }

    // ── Validate (full conformance) ──────────────────────────────────────────

    [Fact]
    public void Validate_NullFrontmatter_ReportsMissingFrontmatter()
    {
        var violations = SkillFrontmatterSpec.Validate(null, "my-skill");
        Assert.Single(violations);
        Assert.Contains("frontmatter", violations[0]);
    }

    [Fact]
    public void Validate_NameDoesNotMatchDirectory_ReportsMismatch()
    {
        var fm = new SkillFrontmatter("my-skill", "A description.", null, null, null, null);
        var violations = SkillFrontmatterSpec.Validate(fm, "different-dir");
        Assert.Contains(violations, v => v.Contains("does not match"));
    }

    [Fact]
    public void Validate_FullyCompliant_ReturnsNoViolations()
    {
        var fm = new SkillFrontmatter("my-skill", "A description.", "MIT", "Requires docker", null, null);
        var violations = SkillFrontmatterSpec.Validate(fm, "my-skill");
        Assert.Empty(violations);
    }

    // ── ToSlug ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PDF Processing", "pdf-processing")]
    [InlineData("My Bad Skill!!", "my-bad-skill")]
    [InlineData("  leading and trailing  ", "leading-and-trailing")]
    [InlineData("already-a-slug", "already-a-slug")]
    public void ToSlug_ProducesValidSlug(string input, string expected)
    {
        var slug = SkillFrontmatterSpec.ToSlug(input);
        Assert.Equal(expected, slug);
        Assert.True(SkillFrontmatterSpec.ValidateName(slug, out _));
    }

    // ── WithCanonicalName ────────────────────────────────────────────────────

    [Fact]
    public void WithCanonicalName_ReplacesExistingNameField()
    {
        var content = "---\nname: My Bad Skill!!\ndescription: A description.\n---\n\nBody";
        var rewritten = SkillFrontmatterSpec.WithCanonicalName(content, "my-bad-skill");

        var fm = SkillFrontmatterSpec.TryParse(rewritten);
        Assert.Equal("my-bad-skill", fm!.Name);
        Assert.Equal("A description.", fm.Description); // untouched
        Assert.Contains("Body", rewritten);              // body untouched
    }

    [Fact]
    public void WithCanonicalName_NoExistingNameField_InsertsOne()
    {
        var content = "---\ndescription: A description.\n---\n\nBody";
        var rewritten = SkillFrontmatterSpec.WithCanonicalName(content, "new-slug");

        var fm = SkillFrontmatterSpec.TryParse(rewritten);
        Assert.Equal("new-slug", fm!.Name);
        Assert.Equal("A description.", fm.Description);
    }

    [Fact]
    public void WithCanonicalName_NoFrontmatter_ReturnsContentUnchanged()
    {
        const string content = "# No frontmatter\n\nJust body text.";
        Assert.Equal(content, SkillFrontmatterSpec.WithCanonicalName(content, "some-slug"));
    }
}
