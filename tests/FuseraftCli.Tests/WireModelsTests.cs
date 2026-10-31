using System.Text.Json;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Knowledge;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Context;

namespace FuseraftCli.Tests;

/// <summary>
/// Pins the field names of every wire record (docs/protocol.md). Adding a field means adding it here;
/// removing or renaming one is a breaking change that needs a WireProtocol.Version bump.
/// </summary>
public sealed class WireModelsTests
{
    private static string[] Keys(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, WireProtocol.JsonOpts));
        return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WireSession_Fields()
    {
        var wire = WireSession.From(new SessionIndexEntry
        {
            SessionId = "s1", Task = "Build it", WorkingDirectory = "/w", ConfigPath = "/w/o.yaml",
            StartedAt = T0, LastUpdatedAt = T0, IsComplete = true, TurnCount = 4,
        });

        Assert.Equal(
            ["sessionId", "task", "workingDirectory", "configPath", "startedAt", "lastUpdatedAt", "isComplete", "turnCount"],
            Keys(wire));
        Assert.Equal(4, wire.TurnCount);
        Assert.True(wire.IsComplete);
    }

    private static ReplSessionSnapshot Snapshot(string? title = null, string? autoTitle = null, params ReplSerializedMessage[] history) => new()
    {
        SessionId = "r1", ModelId = "m", Cwd = "/w", Title = title, AutoTitle = autoTitle,
        StartedAt = T0, LastUpdatedAt = T0, TurnIndex = 2, History = [.. history],
    };

    private static ReplSerializedMessage Msg(string role, params ReplSerializedContent[] contents) =>
        new() { Role = role, Contents = [.. contents] };

    [Fact]
    public void WireReplSession_Fields_MatchVsCodeSessionInfo()
    {
        // Same names as fuseraft-vscode's ReplSessionInfo, so the extension can switch to this listing as-is.
        Assert.Equal(
            ["sessionId", "modelId", "cwd", "title", "autoTitle", "startedAt", "lastUpdatedAt", "turnIndex", "firstUserMessage"],
            Keys(WireReplSession.From(Snapshot())));
    }

    [Fact]
    public void WireReplSession_FirstUserMessage_SkipsSystemAndNonTextContent()
    {
        var wire = WireReplSession.From(Snapshot(history:
        [
            Msg("system", new ReplSerializedContent { Text = "you are" }),
            Msg("user", new ReplSerializedContent { Type = "image" }, new ReplSerializedContent { Text = "fix the bug" }),
            Msg("user", new ReplSerializedContent { Text = "second" }),
        ]));

        Assert.Equal("fix the bug", wire.FirstUserMessage);
    }

    [Fact]
    public void WireReplSession_FirstUserMessage_IsCapped()
    {
        var wire = WireReplSession.From(Snapshot(history:
            [Msg("user", new ReplSerializedContent { Text = new string('x', WireReplSession.MaxPreviewChars + 50) })]));

        Assert.Equal(WireReplSession.MaxPreviewChars, wire.FirstUserMessage.Length);
    }

    [Fact]
    public void WireReplSession_BlankTitlesBecomeNull()
    {
        var wire = WireReplSession.From(Snapshot(title: "  ", autoTitle: ""));
        Assert.Null(wire.Title);
        Assert.Null(wire.AutoTitle);
        Assert.Equal(string.Empty, wire.FirstUserMessage);
    }

    [Fact]
    public void WireMemory_Fields_OmitFilePath()
    {
        var wire = WireMemory.From(new MemoryEntry
        {
            Guid = "g", Name = "n", Type = "user", Description = "d", Body = "b", FilePath = "/secret/home/path.md",
        });

        Assert.Equal(["guid", "name", "type", "description", "body"], Keys(wire));
    }

    [Fact]
    public void WireContextItem_Fields_AndTotalSize()
    {
        var wire = WireContextItem.From(new ContextItem
        {
            Name = "spec", Description = "d", SourcePath = "/src", ImportedAt = T0,
            Files = [new ContextFileEntry("a.md", 10), new ContextFileEntry("b.md", 32)],
        });

        Assert.Equal(["name", "description", "sourcePath", "importedAt", "sizeBytes", "files"], Keys(wire));
        Assert.Equal(42, wire.SizeBytes);
        Assert.Equal(["relativePath", "sizeBytes"], Keys(wire.Files[0]));
    }

    [Fact]
    public void WireScheduledJob_Fields()
    {
        var wire = WireScheduledJob.From(new ScheduledJob { Name = "nightly", Cron = "0 2 * * *", Task = "t" }, T0);

        Assert.Equal(
            ["name", "description", "cron", "task", "config", "workDir", "enabled", "due", "createdAt", "lastRun", "nextRun"],
            Keys(wire));
    }

    [Theory]
    [InlineData(true, -1, true)]   // enabled, next run in the past
    [InlineData(true, 1, false)]   // enabled, next run in the future
    [InlineData(false, -1, false)] // disabled jobs are never due
    public void WireScheduledJob_Due(bool enabled, int hoursFromNow, bool expected)
    {
        var now = new DateTimeOffset(T0);
        var job = new ScheduledJob { Name = "j", Enabled = enabled, NextRun = now.AddHours(hoursFromNow) };
        Assert.Equal(expected, WireScheduledJob.From(job, now).Due);
    }

    [Fact]
    public void WireScheduledJob_NeverScheduled_IsNotDue()
    {
        Assert.False(WireScheduledJob.From(new ScheduledJob { Name = "j", NextRun = null }, new DateTimeOffset(T0)).Due);
    }

    [Fact]
    public void WirePlugin_Fields()
    {
        var fn = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => 1, "ping", "Pings.");
        var wire = WirePlugin.From("Net", "builtin", [fn]);

        Assert.Equal(["name", "source", "functions"], Keys(wire));
        Assert.Equal(["name", "description"], Keys(wire.Functions[0]));
        Assert.Equal("ping", wire.Functions[0].Name);
    }

    [Fact]
    public void WireModel_Fields()
    {
        Assert.Equal(["id", "current"], Keys(new WireModel("m", true)));
    }
}
