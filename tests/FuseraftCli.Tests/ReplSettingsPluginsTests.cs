using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReplSettings.EnabledPlugins"/>: parsing, trimming,
/// case-insensitivity, and empty/null inputs.
/// </summary>
public sealed class ReplSettingsPluginsTests
{
    private static ReplSettings With(string? plugins) => new() { Plugins = plugins };

    // ── null / empty ─────────────────────────────────────────────────────────

    [Fact]
    public void EnabledPlugins_NullPlugins_ReturnsEmptySet()
    {
        var set = With(null).EnabledPlugins;
        Assert.Empty(set);
    }

    [Fact]
    public void EnabledPlugins_EmptyString_ReturnsEmptySet()
    {
        var set = With("").EnabledPlugins;
        Assert.Empty(set);
    }

    [Fact]
    public void EnabledPlugins_WhitespaceOnly_ReturnsEmptySet()
    {
        var set = With("   ").EnabledPlugins;
        Assert.Empty(set);
    }

    // ── single plugin ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Changes")]
    [InlineData("Chatroom")]
    [InlineData("SessionContext")]
    [InlineData("Scratchpad")]
    public void EnabledPlugins_SingleKnownPlugin_ContainsThatPlugin(string name)
    {
        Assert.Contains(name, With(name).EnabledPlugins);
    }

    // ── case-insensitivity ───────────────────────────────────────────────────

    [Theory]
    [InlineData("changes")]
    [InlineData("CHANGES")]
    [InlineData("Changes")]
    [InlineData("cHaNgEs")]
    public void EnabledPlugins_Changes_CaseInsensitive(string input)
    {
        Assert.Contains("Changes", With(input).EnabledPlugins);
    }

    [Theory]
    [InlineData("chatroom")]
    [InlineData("CHATROOM")]
    [InlineData("Chatroom")]
    public void EnabledPlugins_Chatroom_CaseInsensitive(string input)
    {
        Assert.Contains("Chatroom", With(input).EnabledPlugins);
    }

    [Theory]
    [InlineData("sessioncontext")]
    [InlineData("SESSIONCONTEXT")]
    [InlineData("SessionContext")]
    public void EnabledPlugins_SessionContext_CaseInsensitive(string input)
    {
        Assert.Contains("SessionContext", With(input).EnabledPlugins);
    }

    [Theory]
    [InlineData("scratchpad")]
    [InlineData("SCRATCHPAD")]
    [InlineData("Scratchpad")]
    public void EnabledPlugins_Scratchpad_CaseInsensitive(string input)
    {
        Assert.Contains("Scratchpad", With(input).EnabledPlugins);
    }

    // ── multiple plugins ─────────────────────────────────────────────────────

    [Fact]
    public void EnabledPlugins_AllFour_ContainsAll()
    {
        var set = With("Changes,Chatroom,SessionContext,Scratchpad").EnabledPlugins;
        Assert.Contains("Changes",       set);
        Assert.Contains("Chatroom",      set);
        Assert.Contains("SessionContext", set);
        Assert.Contains("Scratchpad",    set);
    }

    [Fact]
    public void EnabledPlugins_AllFourLowercase_ContainsAll()
    {
        var set = With("changes,chatroom,sessioncontext,scratchpad").EnabledPlugins;
        Assert.Contains("Changes",        set);
        Assert.Contains("Chatroom",       set);
        Assert.Contains("SessionContext", set);
        Assert.Contains("Scratchpad",     set);
    }

    [Fact]
    public void EnabledPlugins_TwoPlugins_ContainsBothNotOthers()
    {
        var set = With("Changes,Scratchpad").EnabledPlugins;
        Assert.Contains("Changes",    set);
        Assert.Contains("Scratchpad", set);
        Assert.DoesNotContain("Chatroom",       set);
        Assert.DoesNotContain("SessionContext", set);
    }

    // ── whitespace trimming ──────────────────────────────────────────────────

    [Fact]
    public void EnabledPlugins_SpacesAroundNames_TrimmedCorrectly()
    {
        var set = With("  Changes , Chatroom  ").EnabledPlugins;
        Assert.Contains("Changes",  set);
        Assert.Contains("Chatroom", set);
    }

    [Fact]
    public void EnabledPlugins_EmptySegments_Ignored()
    {
        var set = With("Changes,,Scratchpad,").EnabledPlugins;
        Assert.Contains("Changes",    set);
        Assert.Contains("Scratchpad", set);
        Assert.Equal(2, set.Count);
    }

    // ── unknown names ────────────────────────────────────────────────────────

    [Fact]
    public void EnabledPlugins_UnknownName_DoesNotThrow()
    {
        var ex = Record.Exception(() => With("NonExistentPlugin").EnabledPlugins);
        Assert.Null(ex);
    }

    [Fact]
    public void EnabledPlugins_UnknownNameMixedWithKnown_KnownPresent()
    {
        var set = With("Changes,NonExistentPlugin").EnabledPlugins;
        Assert.Contains("Changes",           set);
        Assert.Contains("NonExistentPlugin", set);
    }
}
