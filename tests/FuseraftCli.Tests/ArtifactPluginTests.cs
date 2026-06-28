using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ArtifactPlugin"/> — the generic, fixed-target-path artifact writer
/// shared by every recon/triage-style agent across the init templates. Replaces the former
/// per-artifact ReconPluginTests/PreflightPluginTests/AuditPluginTests now that all four
/// registrations (Conventions, DiscoveryBrief, Preflight, AuditFindings) are the same class.
/// </summary>
public sealed class ArtifactPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ArtifactPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_artifact_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _path = Path.Combine(_root, "nested", "artifact.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ArtifactPlugin NewPlugin(ArtifactFormat format = ArtifactFormat.Json) =>
        new(_path, format, "write_file_test_artifact", "Write the test artifact.");

    [Fact]
    public async Task WriteFile_ValidJson_WritesExactContentVerbatim()
    {
        var plugin = NewPlugin();
        const string content = """{"language":"go","naming_patterns":["*_test.go"]}""";

        var result = await plugin.WriteFileAsync(content, "json");

        Assert.StartsWith("[OK]", result);
        Assert.Equal(content, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task WriteFile_MalformedJson_RejectedWithoutWriting()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileAsync("{not valid json", "json");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not valid JSON", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task WriteFile_ValidYaml_Accepted()
    {
        var plugin = NewPlugin(ArtifactFormat.Yaml);

        var result = await plugin.WriteFileAsync("key: value\nlist:\n  - a\n  - b\n", "yaml");

        Assert.StartsWith("[OK]", result);
    }

    [Fact]
    public async Task WriteFile_MalformedYaml_RejectedWithoutWriting()
    {
        var plugin = NewPlugin(ArtifactFormat.Yaml);

        var result = await plugin.WriteFileAsync("key: [unterminated", "yaml");

        Assert.StartsWith("[ERROR]", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task WriteFile_Markdown_AcceptsAnyText()
    {
        var plugin = NewPlugin(ArtifactFormat.Md);

        var result = await plugin.WriteFileAsync("# Report\n\nNo required structure here.", "md");

        Assert.StartsWith("[OK]", result);
    }

    [Fact]
    public async Task WriteFile_FormatParamMismatchesConfiguredFormat_Rejected()
    {
        var plugin = NewPlugin(ArtifactFormat.Json); // configured as json

        var result = await plugin.WriteFileAsync("some text", "md"); // model claims md

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("must be written as 'json'", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task WriteFile_UnknownFormatValue_Rejected()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileAsync("{}", "xml");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("md, json, yaml", result);
    }

    [Fact]
    public async Task WriteFile_FormatParamIsCaseInsensitive()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileAsync("{}", "JSON");

        Assert.StartsWith("[OK]", result);
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_path)));

        var plugin = NewPlugin();
        await plugin.WriteFileAsync("{}", "json");

        Assert.True(File.Exists(_path));
    }

    // ── Registration identity ──────────────────────────────────────────────

    [Fact]
    public void GetFunctionsFromObject_UsesInstanceToolNameAndDescription_NotClassName()
    {
        var plugin = new ArtifactPlugin(_path, ArtifactFormat.Json, "write_file_conventions", "Write the convention profile.");

        var functions = PluginRegistry.GetFunctionsFromObject(plugin);

        Assert.Single(functions);
        Assert.Equal("write_file_conventions", functions[0].Name);
        Assert.Equal("Write the convention profile.", functions[0].Description);
    }

    [Fact]
    public void GetFunctionsFromObject_DifferentInstances_GetDistinctToolNames()
    {
        var conventions = new ArtifactPlugin(_path, ArtifactFormat.Json, "write_file_conventions", "Write conventions.");
        var brief        = new ArtifactPlugin(_path, ArtifactFormat.Json, "write_file_discovery_brief", "Write the brief.");

        var conventionsTool = PluginRegistry.GetFunctionsFromObject(conventions)[0];
        var briefTool       = PluginRegistry.GetFunctionsFromObject(brief)[0];

        // Two instances of the same class, registered for two different agents/artifacts,
        // must never collide on tool name — this is the property the old split-class design
        // (ReconPlugin/PreflightPlugin/AuditPlugin) existed to guarantee.
        Assert.NotEqual(conventionsTool.Name, briefTool.Name);
    }
}
