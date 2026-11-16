using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class HandoffRouteFunctionTests
{
    private static AIFunction Plain() =>
        PluginRegistry.GetFunctionsFromObject(new HandoffPlugin()).Single(f => f.Name == HandoffPlugin.FunctionName);

    private static JsonElement RouteKeyword(AIFunction f) =>
        f.JsonSchema.GetProperty("properties").GetProperty(HandoffPlugin.ArgumentName);

    [Fact]
    public void RouteKeyword_IsAnEnumOfTheRoutes()
    {
        var f = new HandoffRouteFunction(Plain(), [new("Tester", "hand off to Tester."), new("end", "finish the run.")]);

        Assert.Equal(["Tester", "end"], RouteKeyword(f).GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void RouteKeyword_DescribesEachRoute()
    {
        var f = new HandoffRouteFunction(Plain(), [new("Tester", "hand off to Tester when the build passes.")]);
        Assert.Contains("\"Tester\": hand off to Tester when the build passes.", RouteKeyword(f).GetProperty("description").GetString());
    }

    [Fact]
    public void NameAndOtherParameters_AreUnchanged()
    {
        var plain = Plain();
        var f = new HandoffRouteFunction(plain, [new("end", "finish the run.")]);

        Assert.Equal(plain.Name, f.Name);
        Assert.True(f.JsonSchema.GetProperty("properties").TryGetProperty(HandoffPlugin.GoalArgumentName, out _));
        Assert.False(RouteKeyword(plain).TryGetProperty("enum", out _));
    }
}
