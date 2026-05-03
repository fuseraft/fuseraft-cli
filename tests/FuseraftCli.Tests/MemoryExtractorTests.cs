using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

public sealed class MemoryExtractorTests
{
    // Parse

    [Fact]
    public void Parse_ValidJson_ReturnsEntries()
    {
        var json = """
            [
              {
                "name": "user_prefs",
                "description": "User prefers terse responses",
                "type": "user",
                "body": "The user explicitly said they want concise answers."
              }
            ]
            """;

        var result = MemoryExtractor.Parse(json);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("user_prefs", result[0].Name);
        Assert.Equal("user",       result[0].Type);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsEmptyList()
    {
        var result = MemoryExtractor.Parse("[]");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_EmptyArrayWithWhitespace_ReturnsEmptyList()
    {
        var result = MemoryExtractor.Parse("  [ ]  ");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_ProseWithNoArray_ReturnsNull()
    {
        var result = MemoryExtractor.Parse("There is nothing worth saving from this session.");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        var result = MemoryExtractor.Parse(string.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        var result = MemoryExtractor.Parse("[{\"name\": \"x\", broken json");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_JsonWithEmptyName_FiltersEntry()
    {
        var json = """[{"name": "", "description": "d", "type": "project", "body": "body"}]""";
        var result = MemoryExtractor.Parse(json);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_JsonWithEmptyBody_FiltersEntry()
    {
        var json = """[{"name": "x", "description": "d", "type": "project", "body": ""}]""";
        var result = MemoryExtractor.Parse(json);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_JsonWrappedInMarkdownFence_StillFindsArray()
    {
        var text = "```json\n[{\"name\":\"k\",\"description\":\"d\",\"type\":\"project\",\"body\":\"b\"}]\n```";
        var result = MemoryExtractor.Parse(text);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("k", result[0].Name);
    }

    [Fact]
    public void Parse_UnknownType_DefaultsToProject()
    {
        var json = """[{"name": "x", "description": "d", "type": "unknown_type", "body": "b"}]""";
        var result = MemoryExtractor.Parse(json);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("project", result![0].Type);
    }

    [Theory]
    [InlineData("user",      "user")]
    [InlineData("feedback",  "feedback")]
    [InlineData("reference", "reference")]
    [InlineData("project",   "project")]
    [InlineData("USER",      "user")]
    [InlineData("FEEDBACK",  "feedback")]
    public void Parse_TypeNormalization(string inputType, string expectedType)
    {
        var json = $$"""[{"name":"x","description":"d","type":"{{inputType}}","body":"b"}]""";
        var result = MemoryExtractor.Parse(json);
        Assert.NotNull(result);
        Assert.Equal(expectedType, result![0].Type);
    }

    [Fact]
    public void Parse_MultipleEntries_ReturnsAll()
    {
        var json = """
            [
              {"name": "a", "description": "d1", "type": "user",    "body": "b1"},
              {"name": "b", "description": "d2", "type": "project", "body": "b2"},
              {"name": "c", "description": "d3", "type": "feedback","body": "b3"}
            ]
            """;

        var result = MemoryExtractor.Parse(json);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public void Parse_ProseBeforeAndAfterArray_ExtractsArray()
    {
        var text = "Here are the memories:\n[{\"name\":\"x\",\"description\":\"d\",\"type\":\"project\",\"body\":\"b\"}]\nDone.";
        var result = MemoryExtractor.Parse(text);
        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void Parse_ProseWithBracketsBeforeArray_ExtractsArray()
    {
        // Prose contains brackets ("these [important] things") before the JSON array.
        // Using LastIndexOf('[') means we still find the actual array start.
        var text = "Here are these [important] things: [{\"name\":\"k\",\"description\":\"d\",\"type\":\"project\",\"body\":\"b\"}]";
        var result = MemoryExtractor.Parse(text);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("k", result![0].Name);
    }

    // BuildExcerpt

    [Fact]
    public void BuildExcerpt_InsertsBlanksBeforeUserTurns()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User,      "First question"),
            new(ChatRole.Assistant, "First answer"),
            new(ChatRole.User,      "Second question"),
            new(ChatRole.Assistant, "Second answer"),
        };

        var excerpt = MemoryExtractor.BuildExcerpt(history);

        // First user turn has no leading blank; second does.
        var lines = excerpt.Split('\n');
        var firstUser  = Array.FindIndex(lines, l => l.StartsWith("[user]: First"));
        var secondUser = Array.FindIndex(lines, l => l.StartsWith("[user]: Second"));
        Assert.True(firstUser >= 0);
        Assert.True(secondUser > firstUser);
        // The line immediately before the second [user] turn should be blank.
        Assert.Equal(string.Empty, lines[secondUser - 1]);
    }

    [Fact]
    public void BuildExcerpt_SkipsSystemAndToolMessages()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System,    "System instructions"),
            new(ChatRole.User,      "Hello"),
            new(ChatRole.Tool,      "tool result"),
            new(ChatRole.Assistant, "Hi there"),
        };

        var excerpt = MemoryExtractor.BuildExcerpt(history);

        Assert.DoesNotContain("System instructions", excerpt);
        Assert.DoesNotContain("tool result",         excerpt);
        Assert.Contains("[user]: Hello",             excerpt);
        Assert.Contains("[assistant]: Hi there",     excerpt);
    }

    [Fact]
    public void BuildExcerpt_EmptyHistory_ReturnsEmpty()
    {
        var excerpt = MemoryExtractor.BuildExcerpt([]);
        Assert.Equal(string.Empty, excerpt);
    }

    [Fact]
    public void BuildExcerpt_OnlySystemMessages_ReturnsEmpty()
    {
        var history = new List<ChatMessage> { new(ChatRole.System, "Sys") };
        var excerpt = MemoryExtractor.BuildExcerpt(history);
        Assert.Equal(string.Empty, excerpt);
    }
}
