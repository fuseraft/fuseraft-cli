using System.Text.Json;
using fuseraft.Core.Skills;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="FuseraftSkillsSources.TryFlattenArgumentsObject"/>: the recovery
/// path for a model calling <c>run_skill_script</c> with a flag/value object instead of the
/// flat string array the tool actually expects.
/// </summary>
public sealed class FuseraftSkillsSourcesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TryFlattenArgumentsObject_StringValues_ProducesFlagValuePairs()
    {
        var obj = Parse("""{"--type":"Bug","--title":"Fix the thing"}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.True(ok);
        Assert.Equal(["--type", "Bug", "--title", "Fix the thing"], args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_TrueValue_ProducesBareFlag()
    {
        var obj = Parse("""{"--verbose":true}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.True(ok);
        Assert.Equal(["--verbose"], args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_FalseValue_OmitsFlagEntirely()
    {
        var obj = Parse("""{"--type":"Bug","--verbose":false}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.True(ok);
        Assert.Equal(["--type", "Bug"], args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_NumberValue_UsesRawText()
    {
        var obj = Parse("""{"--priority":1}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.True(ok);
        Assert.Equal(["--priority", "1"], args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_NestedObjectValue_FailsClosed()
    {
        var obj = Parse("""{"--type":"Bug","--meta":{"nested":true}}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.False(ok);
        Assert.Empty(args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_ArrayValue_FailsClosed()
    {
        var obj = Parse("""{"--type":"Bug","--tags":["a","b"]}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.False(ok);
        Assert.Empty(args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_NullValue_FailsClosed()
    {
        var obj = Parse("""{"--type":"Bug","--assignee":null}""");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.False(ok);
        Assert.Empty(args);
    }

    [Fact]
    public void TryFlattenArgumentsObject_EmptyObject_ProducesEmptyArgs()
    {
        var obj = Parse("{}");

        var ok = FuseraftSkillsSources.TryFlattenArgumentsObject(obj, out var args);

        Assert.True(ok);
        Assert.Empty(args);
    }

    [Theory]
    [InlineData("/s/run.py", false, "python3", new[] { "/s/run.py" })]
    [InlineData("C:\\s\\run.py", true, "python", new[] { "C:\\s\\run.py" })]
    [InlineData("/s/run.js", false, "node", new[] { "/s/run.js" })]
    [InlineData("/s/run.sh", false, "bash", new[] { "/s/run.sh" })]
    [InlineData("/s/run.ps1", false, "pwsh", new[] { "/s/run.ps1" })]
    public void ResolveCommand_KnownInterpreterExtensions_PrependInterpreter(string path, bool isWindows, string fileName, string[] leading)
    {
        var (file, args) = FuseraftSkillsSources.ResolveCommand(path, isWindows);

        Assert.Equal(fileName, file);
        Assert.Equal(leading, args);
    }

    [Fact]
    public void ResolveCommand_CSharpFile_RunsThroughDotnetWithArgSeparator()
    {
        // The trailing "--" is what keeps a script's own --flags away from `dotnet run`.
        var (file, args) = FuseraftSkillsSources.ResolveCommand("/skills/build-docx/scripts/md2docx.cs", isWindows: false);

        Assert.Equal("dotnet", file);
        Assert.Equal(["run", "/skills/build-docx/scripts/md2docx.cs", "--"], args);
    }

    [Theory]
    [InlineData("/s/run")]
    [InlineData("/s/run.csx")]
    [InlineData("/s/run.rb")]
    public void ResolveCommand_UnknownExtension_ExecutesScriptDirectly(string path)
    {
        var (file, args) = FuseraftSkillsSources.ResolveCommand(path, isWindows: false);

        Assert.Equal(path, file);
        Assert.Empty(args);
    }
}
