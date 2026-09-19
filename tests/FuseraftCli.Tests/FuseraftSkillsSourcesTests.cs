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
}
