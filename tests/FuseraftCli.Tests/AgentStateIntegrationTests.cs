using System.Collections.Generic;
using Xunit;

using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;

namespace FuseraftCli.Tests;

public sealed class AgentStateIntegrationTests
{
    [Fact]
    public void ThreeAgentChain_StateAdvancesCorrectly()
    {
        // Planner produces v0
        var plannerData = new Dictionary<string, object?>
        {
            ["task"] = "implement integration test",
            ["priority"] = "high"
        };
        var v0 = new AgentState
        {
            Version = 0,
            CreatedBy = "Planner",
            CreatedAt = DateTimeOffset.UtcNow,
            Data = plannerData
        };

        // Developer produces v1
        var v1 = StateHandoff.Advance(v0, "Developer", new Dictionary<string, object?>
        {
            ["status"] = "in_progress",
            ["files_changed"] = 3
        });

        // Reviewer produces v2
        var v2 = StateHandoff.Advance(v1, "Reviewer", new Dictionary<string, object?>
        {
            ["status"] = "approved"
        });

        Assert.Equal(0, v0.Version);
        Assert.Equal("Planner", v0.CreatedBy);
        Assert.Equal(1, v1.Version);
        Assert.Equal("Developer", v1.CreatedBy);
        Assert.Equal(2, v2.Version);
        Assert.Equal("Reviewer", v2.CreatedBy);

        Assert.Equal("implement integration test", v2.Data["task"]);
        Assert.Equal("high", v2.Data["priority"]);
        Assert.Equal("in_progress", v1.Data["status"]);
        Assert.Equal("approved", v2.Data["status"]);
        Assert.Equal(3, (int)v2.Data["files_changed"]!);

        Assert.False(v0.Data.ContainsKey("status"));
    }
}