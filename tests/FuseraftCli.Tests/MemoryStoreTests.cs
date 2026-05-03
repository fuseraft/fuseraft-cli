using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_mem_{Guid.NewGuid():N}");

    public MemoryStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose()     => Directory.Delete(_dir, recursive: true);

    // ParseFile

    [Fact]
    public void ParseFile_HappyPath_ReturnsEntry()
    {
        var text = "---\nname: my_entry\ndescription: A fact\ntype: project\n---\n\nBody text here.\n";
        var entry = MemoryStore.ParseFile(text, "/mem/my_entry.md");

        Assert.NotNull(entry);
        Assert.Equal("my_entry", entry.Name);
        Assert.Equal("A fact", entry.Description);
        Assert.Equal("project", entry.Type);
        Assert.Equal("Body text here.", entry.Body);
        Assert.Equal("my_entry.md", entry.FilePath);
    }

    [Fact]
    public void ParseFile_BodyContainsStandaloneDashes_ParsesCorrectly()
    {
        // A git diff header \n--- a/Foo.cs should NOT be the close delimiter.
        var text = "---\nname: diff_note\ndescription: Note\ntype: project\n---\n\nSee this diff:\n--- a/Foo.cs\n+++ b/Foo.cs\nEnd.\n";
        var entry = MemoryStore.ParseFile(text, "/mem/diff_note.md");

        Assert.NotNull(entry);
        Assert.Contains("--- a/Foo.cs", entry!.Body);
        Assert.Contains("End.", entry.Body);
    }

    [Fact]
    public void ParseFile_BodyContainsHorizontalRule_ParsesCorrectly()
    {
        // A markdown horizontal rule --- on its own line IS the close delimiter, so
        // FormatFile never writes one in the body. But a bare --- with trailing text is fine.
        var text = "---\nname: note\ndescription: D\ntype: user\n---\n\nPart one.\n--- separator text here\nPart two.\n";
        var entry = MemoryStore.ParseFile(text, "/mem/note.md");

        Assert.NotNull(entry);
        Assert.Contains("Part one.", entry!.Body);
        Assert.Contains("separator text here", entry.Body);
        Assert.Contains("Part two.", entry.Body);
    }

    [Fact]
    public void ParseFile_NoOpenDelimiter_ReturnsNull()
    {
        var entry = MemoryStore.ParseFile("name: foo\ndescription: bar\n", "/mem/x.md");
        Assert.Null(entry);
    }

    [Fact]
    public void ParseFile_NoCloseDelimiter_ReturnsNull()
    {
        var entry = MemoryStore.ParseFile("---\nname: foo\ndescription: bar\n", "/mem/x.md");
        Assert.Null(entry);
    }

    [Fact]
    public void ParseFile_EmptyName_ReturnsNull()
    {
        var entry = MemoryStore.ParseFile("---\nname:  \ndescription: bar\ntype: project\n---\n\nbody\n", "/mem/x.md");
        Assert.Null(entry);
    }

    [Fact]
    public void ParseFile_MissingNameKey_ReturnsNull()
    {
        var entry = MemoryStore.ParseFile("---\ndescription: bar\ntype: project\n---\n\nbody\n", "/mem/x.md");
        Assert.Null(entry);
    }

    [Fact]
    public void ParseFile_EmptyBody_ReturnsEntryWithEmptyBody()
    {
        var entry = MemoryStore.ParseFile("---\nname: x\ndescription: d\ntype: user\n---\n\n", "/mem/x.md");
        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry!.Body);
    }

    // SafeFileName

    [Theory]
    [InlineData("auth_role",          "auth_role")]
    [InlineData("Auth Role",          "auth_role")]
    [InlineData("AUTH-MIDDLEWARE",    "auth-middleware")]
    [InlineData("foo.bar",            "foobar")]
    [InlineData("hello world!",       "hello_world")]
    [InlineData("kebab-case",         "kebab-case")]
    [InlineData("123_test",           "123_test")]
    public void SafeFileName_ProducesExpectedResult(string input, string expected)
    {
        Assert.Equal(expected, MemoryStore.SafeFileName(input));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("...")]
    [InlineData("@@@")]
    public void SafeFileName_EmptyAfterSanitization_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => MemoryStore.SafeFileName(input));
    }

    [Fact]
    public void SafeFileName_OnlySpaces_ReturnsUnderscores()
    {
        // Spaces are converted to underscores, not stripped, so a spaces-only
        // input produces underscores and does not throw.
        var result = MemoryStore.SafeFileName("   ");
        Assert.Equal("___", result);
    }

    // FormatFile / round-trip

    [Fact]
    public void FormatFile_RoundTripsWithParseFile()
    {
        var original = new MemoryEntry
        {
            Name        = "round_trip",
            Description = "A round-trip test",
            Type        = "feedback",
            Body        = "This is the body.\nIt has multiple lines.",
            FilePath    = "round_trip.md",
        };

        var formatted = MemoryStore.FormatFile(original);
        var parsed    = MemoryStore.ParseFile(formatted, "/mem/round_trip.md");

        Assert.NotNull(parsed);
        Assert.Equal(original.Name,        parsed!.Name);
        Assert.Equal(original.Description, parsed.Description);
        Assert.Equal(original.Type,        parsed.Type);
        Assert.Equal(original.Body,        parsed.Body);
    }

    [Fact]
    public void FormatFile_BodyWithGitDiff_RoundTrips()
    {
        var entry = new MemoryEntry
        {
            Name        = "diff_memory",
            Description = "Contains a diff",
            Type        = "project",
            Body        = "Changed Foo.cs:\n--- a/Foo.cs\n+++ b/Foo.cs\n@@ -1 +1 @@\n-old\n+new\n",
        };

        var formatted = MemoryStore.FormatFile(entry);
        var parsed    = MemoryStore.ParseFile(formatted, "/mem/diff_memory.md");

        Assert.NotNull(parsed);
        Assert.Equal("diff_memory", parsed!.Name);
        Assert.Contains("--- a/Foo.cs", parsed.Body);
        Assert.Contains("+new", parsed.Body);
    }

    // BuildPromptBlockAsync budget

    [Fact]
    public async Task BuildPromptBlockAsync_FewSmallEntries_IncludesFullBodies()
    {
        var store = StoreAt(_dir);
        await store.SaveAsync(Entry("a", "Alpha", "user",    "Short body A."));
        await store.SaveAsync(Entry("b", "Beta",  "project", "Short body B."));

        var block = await store.BuildPromptBlockAsync();

        Assert.NotNull(block);
        Assert.Contains("Short body A.", block!);
        Assert.Contains("Short body B.", block);
    }

    [Fact]
    public async Task BuildPromptBlockAsync_ManyLargeEntries_CapsAtBudget()
    {
        var store = StoreAt(_dir);

        // Write 30 entries each with a ~500-char body (total ~15 000 chars > 8 000 budget).
        for (int i = 0; i < 30; i++)
            await store.SaveAsync(Entry($"entry_{i:D2}", $"Desc {i}", "project", new string('x', 500)));

        var block = await store.BuildPromptBlockAsync();

        Assert.NotNull(block);
        Assert.True(block!.Length <= 9_000, $"Block length {block.Length} exceeds budget + header headroom");
    }

    [Fact]
    public async Task BuildPromptBlockAsync_EmptyStore_ReturnsNull()
    {
        var block = await StoreAt(_dir).BuildPromptBlockAsync();
        Assert.Null(block);
    }

    // Save / Load / Delete round-trip

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var store = StoreAt(_dir);
        var entry = Entry("test_key", "Test description", "user", "Some body text.");

        await store.SaveAsync(entry);
        var loaded = await store.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(entry.Name,        loaded[0].Name);
        Assert.Equal(entry.Description, loaded[0].Description);
        Assert.Equal(entry.Type,        loaded[0].Type);
        Assert.Equal(entry.Body,        loaded[0].Body);
    }

    [Fact]
    public async Task SaveTwice_UpdatesExistingEntry()
    {
        var store = StoreAt(_dir);
        await store.SaveAsync(Entry("key", "Original", "project", "Body v1"));
        await store.SaveAsync(Entry("key", "Updated",  "project", "Body v2"));

        var loaded = await store.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal("Updated", loaded[0].Description);
        Assert.Equal("Body v2", loaded[0].Body);
    }

    [Fact]
    public async Task Delete_ExistingEntry_RemovesIt()
    {
        var store = StoreAt(_dir);
        await store.SaveAsync(Entry("to_delete", "Desc", "project", "Body"));
        await store.SaveAsync(Entry("to_keep",   "Desc", "project", "Body"));

        var deleted = await store.DeleteAsync("to_delete");
        var loaded  = await store.LoadAllAsync();

        Assert.True(deleted);
        Assert.Single(loaded);
        Assert.Equal("to_keep", loaded[0].Name);
    }

    [Fact]
    public async Task Delete_CaseInsensitive_FindsEntry()
    {
        var store = StoreAt(_dir);
        await store.SaveAsync(Entry("MY_ENTRY", "Desc", "project", "Body"));

        // Delete using different casing.
        var deleted = await store.DeleteAsync("my_entry");

        Assert.True(deleted);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Delete_NonExistentEntry_ReturnsFalse()
    {
        var deleted = await StoreAt(_dir).DeleteAsync("does_not_exist");
        Assert.False(deleted);
    }

    // Helpers

    private static MemoryStore StoreAt(string dir) => MemoryStore.CreateForTest(dir);

    private static MemoryEntry Entry(string name, string description, string type, string body) =>
        new() { Name = name, Description = description, Type = type, Body = body };
}
