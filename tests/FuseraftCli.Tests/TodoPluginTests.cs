using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="TodoPlugin"/>: write/read round-trip, validation, and the
/// wholesale-replace semantics the REPL system prompt tells the model to rely on.
/// </summary>
public sealed class TodoPluginTests
{
    [Fact]
    public void Read_Empty_ReturnsEmptyMarker()
    {
        var plugin = new TodoPlugin();
        Assert.Equal("[EMPTY] No todo items.", plugin.Read());
    }

    [Fact]
    public void Write_ThenRead_RoundTripsItems()
    {
        var plugin = new TodoPlugin();
        plugin.Write("""[{"content":"Read entry point","status":"completed"},{"content":"Map request flow","status":"in_progress"}]""");

        var read = plugin.Read();
        Assert.Contains("[x] Read entry point", read);
        Assert.Contains("[~] Map request flow", read);
    }

    [Fact]
    public void Write_PendingItem_UsesEmptyBoxGlyph()
    {
        var plugin = new TodoPlugin();
        var result = plugin.Write("""[{"content":"Not started yet","status":"pending"}]""");
        Assert.Contains("[ ] Not started yet", result);
    }

    [Fact]
    public void Write_SecondCall_ReplacesEntireList()
    {
        var plugin = new TodoPlugin();
        plugin.Write("""[{"content":"First plan item","status":"pending"}]""");
        plugin.Write("""[{"content":"Second plan item","status":"pending"}]""");

        var read = plugin.Read();
        Assert.DoesNotContain("First plan item", read);
        Assert.Contains("Second plan item", read);
    }

    [Fact]
    public void Write_EmptyArray_ClearsList()
    {
        var plugin = new TodoPlugin();
        plugin.Write("""[{"content":"Something","status":"pending"}]""");
        plugin.Write("[]");

        Assert.Equal("[EMPTY] No todo items.", plugin.Read());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"content\":\"missing array brackets\"}")]
    public void Write_MalformedJson_ReturnsError(string malformed)
    {
        var plugin = new TodoPlugin();
        var result = plugin.Write(malformed);
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void Write_EmptyContent_ReturnsError()
    {
        var plugin = new TodoPlugin();
        var result = plugin.Write("""[{"content":"","status":"pending"}]""");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void Write_InvalidStatus_ReturnsError()
    {
        var plugin = new TodoPlugin();
        var result = plugin.Write("""[{"content":"Something","status":"done"}]""");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void Write_InvalidItem_DoesNotMutateExistingList()
    {
        var plugin = new TodoPlugin();
        plugin.Write("""[{"content":"Valid item","status":"pending"}]""");
        plugin.Write("""[{"content":"Bad","status":"nope"}]""");

        Assert.Contains("Valid item", plugin.Read());
    }

    [Fact]
    public void Snapshot_ReflectsLastWrite()
    {
        var plugin = new TodoPlugin();
        plugin.Write("""[{"content":"A","status":"completed"},{"content":"B","status":"pending"}]""");

        var snapshot = plugin.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("A", snapshot[0].Content);
        Assert.Equal("completed", snapshot[0].Status);
    }
}
