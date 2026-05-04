using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireBriefValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_brief_{Guid.NewGuid():N}");
    private string BriefPath => Path.Combine(_dir, "brief.json");
    private static readonly IList<ChatMessage> NoHistory = [];

    public RequireBriefValidatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RequireBriefValidator Validator() => new(BriefPath);

    private async Task WriteJson(object content)
        => await File.WriteAllTextAsync(BriefPath, JsonSerializer.Serialize(content));

    // Check 1 — file existence

    [Fact]
    public async Task BriefMissing_Fails()
    {
        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    // Check 2 — JSON validity

    [Fact]
    public async Task InvalidJson_Fails()
    {
        await File.WriteAllTextAsync(BriefPath, "not json {{{");

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("invalid JSON", result.ErrorMessage);
    }

    // Check 3 — goal

    [Fact]
    public async Task MissingGoal_Fails()
    {
        await WriteJson(new
        {
            files_to_change = new[] { "a.go" },
            acceptance_criteria = new[] { "it compiles" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("goal", result.ErrorMessage);
    }

    [Fact]
    public async Task WhitespaceGoal_Fails()
    {
        await WriteJson(new
        {
            goal = "   ",
            files_to_change = new[] { "a.go" },
            acceptance_criteria = new[] { "it compiles" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
    }

    // Check 4 — files_to_change

    [Fact]
    public async Task MissingFilesToChange_Fails()
    {
        await WriteJson(new
        {
            goal = "build it",
            acceptance_criteria = new[] { "it compiles" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("files_to_change", result.ErrorMessage);
    }

    [Fact]
    public async Task EmptyFilesToChange_Fails()
    {
        await WriteJson(new
        {
            goal = "build it",
            files_to_change = Array.Empty<object>(),
            acceptance_criteria = new[] { "it compiles" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
    }

    // Check 5 — acceptance_criteria

    [Fact]
    public async Task MissingAcceptanceCriteria_Fails()
    {
        await WriteJson(new
        {
            goal = "build it",
            files_to_change = new[] { "a.go" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("acceptance_criteria", result.ErrorMessage);
    }

    [Fact]
    public async Task EmptyAcceptanceCriteria_Fails()
    {
        await WriteJson(new
        {
            goal = "build it",
            files_to_change = new[] { "a.go" },
            acceptance_criteria = Array.Empty<string>()
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
    }

    // Happy path

    [Fact]
    public async Task CompleteBrief_Passes()
    {
        await WriteJson(new
        {
            goal = "build a todo app",
            files_to_change = new[] { "main.go" },
            acceptance_criteria = new[] { "app starts", "tasks persist" }
        });

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task CompleteBrief_HistoryIsIgnored_Passes()
    {
        // The validator only reads disk — history contents should not affect the result.
        await WriteJson(new
        {
            goal = "build it",
            files_to_change = new[] { "a.go" },
            acceptance_criteria = new[] { "criterion" }
        });
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            new(ChatRole.Assistant, "I will now hand off")
        };

        var result = await Validator().ValidateAsync(history);

        Assert.True(result.IsValid);
    }
}
