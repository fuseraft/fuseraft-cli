using System.Text.Json.Nodes;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class JsonPluginTests
{
    private readonly JsonPlugin _plugin = new();

    // Format

    [Fact]
    public void Format_ValidJson_ReturnsIndentedOutput()
    {
        var result = _plugin.Format("""{"a":1,"b":2}""");

        Assert.Contains('\n', result); // indented output spans multiple lines
        Assert.Contains("\"a\": 1", result);
        Assert.Contains("\"b\": 2", result);
    }

    [Fact]
    public void Format_InvalidJson_ReturnsInvalidMarker()
    {
        var result = _plugin.Format("{not valid json");

        Assert.Contains("[INVALID JSON]", result);
    }

    // Minify

    [Fact]
    public void Minify_ValidJson_RemovesWhitespace()
    {
        var result = _plugin.Minify("""{ "a" : 1 , "b" : 2 }""");

        Assert.Equal("""{"a":1,"b":2}""", result);
    }

    [Fact]
    public void Minify_InvalidJson_ReturnsInvalidMarker()
    {
        var result = _plugin.Minify("{broken");

        Assert.Contains("[INVALID JSON]", result);
    }

    // Get

    [Fact]
    public void Get_DotPath_ReturnsValue()
    {
        var result = _plugin.Get("""{"user":{"name":"Bob"}}""", "user.name");

        Assert.Equal("\"Bob\"", result);
    }

    [Fact]
    public void Get_BracketArrayPath_ReturnsValue()
    {
        var result = _plugin.Get("""{"items":[10,20,30]}""", "items[2]");

        Assert.Equal("30", result);
    }

    [Fact]
    public void Get_MissingPath_ReturnsPathErrorIndicator()
    {
        // "x" is missing on the top-level object, so the walk goes null before
        // the final segment "y" is processed — hits the mid-path null check,
        // not the end-of-loop "[NULL]" fallback.
        var result = _plugin.Get("""{"a":1}""", "x.y");

        Assert.Contains("Path does not exist", result);
    }

    // Keys

    [Fact]
    public void Keys_Object_ReturnsTopLevelKeys()
    {
        var result = _plugin.Keys("""{"a":1,"b":2,"c":3}""");

        Assert.Equal("a, b, c", result);
    }

    [Fact]
    public void Keys_Array_ReturnsLength()
    {
        var result = _plugin.Keys("[1,2,3,4]");

        Assert.Contains("length=4", result);
    }

    [Fact]
    public void Keys_Scalar_ReturnsScalarMarker()
    {
        var result = _plugin.Keys("42");

        Assert.Contains("[SCALAR]", result);
    }

    // Search

    [Fact]
    public void Search_FindsNestedKeyMatch()
    {
        var result = _plugin.Search("""{"user":{"id":1,"name":"Bob"}}""", "name");

        Assert.Contains("user.name", result);
    }

    [Fact]
    public void Search_KeyNotFound_ReturnsNotFoundMarker()
    {
        var result = _plugin.Search("""{"a":1}""", "zzz");

        Assert.Contains("[NOT FOUND]", result);
    }

    // Merge

    [Fact]
    public void Merge_PatchOverridesAndAddsKeys()
    {
        var result = _plugin.Merge("""{"a":1,"b":2}""", """{"b":3,"c":4}""");

        var merged = JsonNode.Parse(result)!.AsObject();
        Assert.Equal(1, (int)merged["a"]!);
        Assert.Equal(3, (int)merged["b"]!);
        Assert.Equal(4, (int)merged["c"]!);
    }

    [Fact]
    public void Merge_BaseIsJsonNull_ReturnsError()
    {
        // JsonNode.Parse("null") returns a null reference, so "?.AsObject()"
        // short-circuits and the ArgumentException("'base' is not a JSON object.")
        // path fires — this is the only way to reach that branch (a JsonArray
        // or scalar body throws inside AsObject() itself, with a different message).
        var result = _plugin.Merge("null", """{"a":1}""");

        Assert.Contains("not a JSON object", result);
    }

    // ToText

    [Fact]
    public void ToText_NestedObject_ReturnsReadableSummary()
    {
        var result = _plugin.ToText("""{"user":{"id":1,"name":"Bob"}}""");

        Assert.Contains("id: 1", result);
        Assert.Contains("name: Bob", result);
    }

    // Validate

    [Fact]
    public void Validate_ValidJson_ReturnsValid()
    {
        var result = _plugin.Validate("""{"a":1}""");

        Assert.Equal("valid", result);
    }

    [Fact]
    public void Validate_InvalidJson_ReturnsInvalidWithDetails()
    {
        var result = _plugin.Validate("{a:1}"); // unquoted key

        Assert.StartsWith("invalid:", result);
    }
}