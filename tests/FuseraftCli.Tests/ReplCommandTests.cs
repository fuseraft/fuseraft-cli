using System.Text.Json;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

public sealed class ReplCommandTests
{
    [Fact]
    public void ReadyEvent_KeepsEveryFieldVsCodeReads_AndAddsProtocolVersion()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            ReplCommand.ReadyEvent("abc123", "model-x", "My chat", null), WireProtocol.JsonOpts));
        var root = doc.RootElement;

        // replPanelProvider.ts and media/repl.js read type, sessionId, model, title, autoTitle, midTurnCommands.
        Assert.Equal("ready", root.GetProperty("type").GetString());
        Assert.Equal("abc123", root.GetProperty("sessionId").GetString());
        Assert.Equal("model-x", root.GetProperty("model").GetString());
        Assert.Equal("My chat", root.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("autoTitle").ValueKind);
        Assert.Contains("/title", root.GetProperty("midTurnCommands").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(WireProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public void ReadyEvent_ListsHelpCommandsForAHostMenu_WithoutTerminalOnlyOnes()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            ReplCommand.ReadyEvent("abc123", "model-x", null, null), WireProtocol.JsonOpts));
        var commands = doc.RootElement.GetProperty("commands").EnumerateArray()
            .Select(c => (Usage: c.GetProperty("usage").GetString()!, Description: c.GetProperty("description").GetString()!))
            .ToList();

        Assert.Contains(commands, c => c.Usage == "/help" && c.Description.Length > 0);
        Assert.Contains(commands, c => c.Usage == "/model <id> [effort]");
        Assert.DoesNotContain(commands, c => c.Usage.StartsWith("/theme") || c.Usage.StartsWith("/dock")
                                          || c.Usage.StartsWith("/paste") || c.Usage == "/exit");
    }

    [Fact]
    public void HostModel_CompletesAConfigThatHasSettingsButNoModel()
    {
        var saved = new UserConfig { Repl = new() { Bell = false } };

        var cfg = ReplCommand.WithHostModel(saved, " claude-x ", "anthropic");

        Assert.Same(saved, cfg);
        Assert.Equal("claude-x", cfg!.ModelId);
        Assert.Equal("anthropic", cfg.Provider);
    }

    [Fact]
    public void HostModel_LeavesAConfiguredModelAlone()
    {
        var saved = new UserConfig { ModelId = "gpt-x", Provider = "openai" };

        var cfg = ReplCommand.WithHostModel(saved, "claude-x", "anthropic");

        Assert.Equal("gpt-x", cfg!.ModelId);
        Assert.Equal("openai", cfg.Provider);
    }

    [Fact]
    public void HostModel_MakesAConfigWhenThereIsNone_AndNothingWithoutAModel()
    {
        Assert.Equal("claude-x", ReplCommand.WithHostModel(null, "claude-x", null)!.ModelId);
        Assert.Null(ReplCommand.WithHostModel(null, null, "anthropic"));
    }
}
