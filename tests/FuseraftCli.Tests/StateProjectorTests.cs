using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

public sealed class StateProjectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public StateProjectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string StatePath() => Path.Combine(_dir, "execution-state.json");

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static async Task WriteStateAsync(string path, ExecutionState state) =>
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(state, JsonOpts));

    private static async Task<ExecutionState> ReadStateAsync(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<ExecutionState>(
            await File.ReadAllTextAsync(path), JsonOpts)!;

    [Fact]
    public async Task Initialize_ResetsStateWhenSessionDiffers()
    {
        var path = StatePath();
        await WriteStateAsync(path, new ExecutionState
        {
            SessionId      = "prior-session",
            Build          = new BuildState { Succeeded = true, Command = "dotnet build" },
            FailedAttempts = [new AttemptRecord { Description = "old attempt", Outcome = "failed" }],
            SignificantChanges = [new FileChangeRecord { Path = "src/Foo.cs", Operation = "written" }],
        });

        var projector = new StateProjector(path, "new-session");
        await projector.InitializeAsync();

        var state = await ReadStateAsync(path);
        Assert.Equal("new-session", state.SessionId);
        Assert.Empty(state.FailedAttempts);
        Assert.Empty(state.SignificantChanges);
        Assert.False(state.Build.Succeeded);
    }

    [Fact]
    public async Task Initialize_PreservesStateWhenSessionMatches()
    {
        var path = StatePath();
        await WriteStateAsync(path, new ExecutionState
        {
            SessionId      = "same-session",
            FailedAttempts = [new AttemptRecord { Description = "prior attempt", Outcome = "failed" }],
        });

        var projector = new StateProjector(path, "same-session");
        await projector.InitializeAsync();

        var state = await ReadStateAsync(path);
        Assert.Equal("same-session", state.SessionId);
        Assert.Single(state.FailedAttempts);
    }

    [Fact]
    public async Task Initialize_IsNoOpWhenFileAbsent()
    {
        var path      = StatePath();
        var projector = new StateProjector(path, "new-session");
        await projector.InitializeAsync(); // must not throw
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ProjectAsync_ResetsStaleSessionAsDefenseInDepth()
    {
        // Even if Initialize was not called, a ProjectAsync with actual invocations
        // must not write the prior session's data.
        var path = StatePath();
        await WriteStateAsync(path, new ExecutionState
        {
            SessionId = "old-session",
            Build     = new BuildState { Succeeded = true },
        });

        var inv = new InvocationRecord(
            Name:      "write_file",
            Args:      new Dictionary<string, object?> { ["path"] = "fwc/Counter.cs" },
            Succeeded: true);

        var projector = new StateProjector(path, "brand-new");
        await projector.ProjectAsync([inv], "Developer", 0, CancellationToken.None);

        var state = await ReadStateAsync(path);
        Assert.Equal("brand-new", state.SessionId);
        Assert.False(state.Build.Succeeded);
        Assert.Single(state.SignificantChanges);
    }
}
