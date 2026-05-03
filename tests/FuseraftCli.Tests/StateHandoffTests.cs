using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="StateHandoff"/> and the <see cref="AgentState"/> record.
/// </summary>
public sealed class StateHandoffTests
{
    // -------------------------------------------------------------------------
    // AgentState.Initial
    // -------------------------------------------------------------------------

    [Fact]
    public void Initial_ProducesVersionZero()
    {
        var state = AgentState.Initial("Planner");

        Assert.Equal(0, state.Version);
        Assert.Equal("Planner", state.CreatedBy);
        Assert.Empty(state.Data);
    }

    // -------------------------------------------------------------------------
    // StateHandoff.Advance — version increment
    // -------------------------------------------------------------------------

    [Fact]
    public void Advance_IncrementsVersionByOne()
    {
        var initial = AgentState.Initial("Planner");

        var next = StateHandoff.Advance(initial, "Developer", new Dictionary<string, object?>());

        Assert.Equal(1, next.Version);
    }

    [Fact]
    public void Advance_SetsNextAgent()
    {
        var initial = AgentState.Initial("Planner");

        var next = StateHandoff.Advance(initial, "Developer", new Dictionary<string, object?>());

        Assert.Equal("Developer", next.CreatedBy);
    }

    [Fact]
    public void Advance_ChainedCalls_VersionMonotonicallyIncreases()
    {
        var s0 = AgentState.Initial("Planner");
        var s1 = StateHandoff.Advance(s0, "Developer", new Dictionary<string, object?>());
        var s2 = StateHandoff.Advance(s1, "Reviewer", new Dictionary<string, object?>());

        Assert.Equal(0, s0.Version);
        Assert.Equal(1, s1.Version);
        Assert.Equal(2, s2.Version);
    }

    // -------------------------------------------------------------------------
    // StateHandoff.Advance — immutability
    // -------------------------------------------------------------------------

    [Fact]
    public void Advance_DoesNotMutateOriginal()
    {
        var initial = AgentState.Initial("Planner");
        var originalVersion = initial.Version;
        var originalAgent   = initial.CreatedBy;

        _ = StateHandoff.Advance(initial, "Developer", new Dictionary<string, object?> { ["x"] = 42 });

        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalAgent, initial.CreatedBy);
        Assert.Empty(initial.Data);
    }

    [Fact]
    public void Advance_MutationsDoNotAffectSourceData()
    {
        var initial = AgentState.Initial("Planner");
        var mutations = new Dictionary<string, object?> { ["key"] = "value" };

        var next = StateHandoff.Advance(initial, "Developer", mutations);

        // Mutating the source dictionary after the call must not change the snapshot.
        mutations["key"] = "changed";
        Assert.Equal("value", next.Data["key"]);
    }

    // -------------------------------------------------------------------------
    // StateHandoff.Advance — data merging
    // -------------------------------------------------------------------------

    [Fact]
    public void Advance_CarriesForwardUnchangedKeys()
    {
        var s0 = new AgentState
        {
            Version   = 0,
            CreatedBy = "Planner",
            CreatedAt = DateTimeOffset.UtcNow,
            Data      = new Dictionary<string, object?> { ["task"] = "build feature", ["priority"] = "high" }
        };

        var s1 = StateHandoff.Advance(s0, "Developer", new Dictionary<string, object?> { ["priority"] = "low" });

        Assert.Equal("build feature", s1.Data["task"]);
        Assert.Equal("low", s1.Data["priority"]);
    }

    [Fact]
    public void Advance_MutationOverwritesExistingKey()
    {
        var s0 = new AgentState
        {
            Version   = 0,
            CreatedBy = "Planner",
            CreatedAt = DateTimeOffset.UtcNow,
            Data      = new Dictionary<string, object?> { ["status"] = "planned" }
        };

        var s1 = StateHandoff.Advance(s0, "Developer", new Dictionary<string, object?> { ["status"] = "in_progress" });

        Assert.Equal("in_progress", s1.Data["status"]);
    }

    [Fact]
    public void Advance_NoMutations_DataIdenticalToSource()
    {
        var s0 = new AgentState
        {
            Version   = 0,
            CreatedBy = "Planner",
            CreatedAt = DateTimeOffset.UtcNow,
            Data      = new Dictionary<string, object?> { ["x"] = 1 }
        };

        var s1 = StateHandoff.Advance(s0, "Developer");

        Assert.Equal(s0.Data["x"], s1.Data["x"]);
        Assert.Single(s1.Data);
    }

}
