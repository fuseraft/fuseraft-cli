using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class HandoffPluginTests
{
    // -----------------------------------------------------------------------
    // HandoffPlugin — tool behaviour
    // -----------------------------------------------------------------------

    [Fact]
    public void Handoff_ReturnsRouteKeywordVerbatim()
    {
        var plugin = new HandoffPlugin();
        Assert.Equal("HANDOFF TO TESTER", plugin.Handoff("HANDOFF TO TESTER"));
        Assert.Equal("APPROVED",          plugin.Handoff("APPROVED"));
    }

    [Fact]
    public void HandoffPlugin_Constants_AreCorrect()
    {
        Assert.Equal("Handoff",       HandoffPlugin.PluginName);
        Assert.Equal("handoff",       HandoffPlugin.FunctionName);
        Assert.Equal("route_keyword", HandoffPlugin.ArgumentName);
        Assert.Equal("goal",          HandoffPlugin.GoalArgumentName);
        Assert.Equal("background",    HandoffPlugin.BackgroundArgumentName);
        Assert.Equal("constraints",   HandoffPlugin.ConstraintsArgumentName);
    }

    [Fact]
    public void Handoff_OptionalDirectiveArgs_DoNotAffectReturnValue()
    {
        // goal/background/constraints exist only for orchestrators to read off the raw
        // FunctionCallContent — the tool method itself always returns route_keyword verbatim
        // regardless of what (if anything) is passed for them.
        var plugin = new HandoffPlugin();

        var result = plugin.Handoff(
            "HANDOFF TO TESTER",
            goal: "Fix the failing test",
            background: "Root cause was a race condition",
            constraints: "Do not change the public API");

        Assert.Equal("HANDOFF TO TESTER", result);
    }
}
