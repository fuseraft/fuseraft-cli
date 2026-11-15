using fuseraft.Cli.Commands;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>Tests for the tool catalog <c>fuseraft settings tools</c> lists without starting a chat.</summary>
public sealed class ReplToolCatalogTests
{
    [Fact]
    public void DescribeTools_NamesToolsAsAChatDoes()
    {
        var live = PluginRegistry.GetFunctionsFromObject(new TodoPlugin()).Select(f => f.Name);

        Assert.Equal(live, PluginRegistry.DescribeTools(typeof(TodoPlugin)).Select(t => t.Name));
    }

    [Fact]
    public void CoreAndExtended_SplitEachPluginsTools()
    {
        var git      = ReplToolCatalog.StaticTools("Git")!.Select(t => t.Name).ToList();
        var extended = ReplToolCatalog.StaticTools("Extended")!.Select(t => t.Name).ToList();

        Assert.Equal(ReplToolCatalog.CoreGitTools.Order(), git.Order());
        Assert.Contains("git_push", extended);
        Assert.Contains("delete_file", extended);
        Assert.Empty(git.Intersect(extended));
    }

    [Fact]
    public void EveryBuiltInCategory_ListsItsTools_ExceptSkills()
    {
        foreach (var c in ReplToolCatalog.Categories)
            if (c.Name == "Skills") Assert.Null(ReplToolCatalog.StaticTools(c.Name));
            else Assert.NotEmpty(ReplToolCatalog.StaticTools(c.Name)!);
    }

    [Theory]
    [InlineData("Read a file. Then more.", "Read a file.")]
    [InlineData("No period here", "No period here")]
    [InlineData("Line one\nstill one. Two.", "Line one still one.")]
    public void FirstSentence_CutsAtTheFirstFullStop(string input, string expected) =>
        Assert.Equal(expected, ReplToolCatalog.FirstSentence(input));

    [Fact]
    public void SettingsCatalog_MarksWhatNewChatsStartWith()
    {
        var categories = SettingsToolsCommand.Catalog(
            new ReplDefaultsConfig { Plugins = ["Http"], DisabledTools = ["Shell", "git_commit", "mcp:docs"] }, ["docs", "wiki"]);

        bool On(string name) => categories.Single(c => c.Name == name).Enabled;
        Assert.False(On("Shell"));
        Assert.True(On("Git"));
        Assert.True(On("Http"));
        Assert.False(On("Extended"));
        Assert.False(On("mcp:docs"));
        Assert.True(On("mcp:wiki"));
        Assert.Null(categories.Single(c => c.Name == "mcp:wiki").Tools);
        Assert.False(categories.Single(c => c.Name == "Git").Tools!.Single(t => t.Name == "git_commit").Enabled);
    }
}
