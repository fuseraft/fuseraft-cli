using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="AskPlugin"/>: the tool name, option cleanup and validation, and how
/// a choice, a typed answer, and a dismissal are reported back to the model.
/// </summary>
public sealed class AskPluginTests
{
    private sealed class Person(string? answer) : IHumanApprovalService
    {
        public (string Question, IReadOnlyList<string> Options, bool AllowOther)? Asked { get; private set; }

        public bool CanAskQuestions => true;

        public Task<string?> PromptQuestionAsync(string question, IReadOnlyList<string> options, bool allowOther, CancellationToken ct)
        {
            Asked = (question, options, allowOther);
            return Task.FromResult(answer);
        }

        public Task<string?> PromptContinueAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptRedirectAsync(string agentName) => Task.FromResult<string?>(null);
        public Task<string?> PromptValidatorStuckAsync(string agentName, string validatorName, int consecutiveFailures, string lastError) => Task.FromResult<string?>(null);
        public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage) => Task.FromResult<string?>(null);
        public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent) => Task.FromResult(false);
        public Task<string?> PromptPostSessionAsync() => Task.FromResult<string?>(null);
        public Task<bool> PromptShellCommandAsync(string command) => Task.FromResult(false);
        public Task<bool> PromptToolActionAsync(string plugin, string action, string detail) => Task.FromResult(false);
        public Task<bool> PromptFileWriteAsync(string action, string path, string oldContent, string newContent) => Task.FromResult(false);
        public Task<string?> PromptPlanReviewAsync(string planText) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void RegistersAsAskUser()
    {
        var fn = Assert.Single(PluginRegistry.GetFunctionsFromObject(new AskPlugin(new Person("A"))));
        Assert.Equal("ask_user", fn.Name);
    }

    [Fact]
    public async Task PassesTheQuestion_AndReportsAChoice()
    {
        var person = new Person("SQLite");

        var result = await new AskPlugin(person).UserAsync("Which database? ", [" Postgres", "SQLite", "", "SQLite"]);

        Assert.Equal("The user chose: SQLite", result);
        Assert.Equal("Which database?", person.Asked!.Value.Question);
        Assert.Equal(["Postgres", "SQLite"], person.Asked.Value.Options);
        Assert.True(person.Asked.Value.AllowOther);
    }

    [Fact]
    public async Task ReportsATypedAnswer_AndADismissal()
    {
        Assert.Equal("The user answered: DuckDB", await new AskPlugin(new Person("DuckDB")).UserAsync("Which?", ["A", "B"]));
        Assert.Contains("dismissed", await new AskPlugin(new Person(null)).UserAsync("Which?", ["A", "B"]));
        Assert.Contains("dismissed", await new AskPlugin(new Person("C")).UserAsync("Which?", ["A", "B"], allow_other: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task RejectsTooFewOrTooManyOptions(int count)
    {
        var person  = new Person("o1");
        var options = Enumerable.Range(1, count).Select(i => $"o{i}").ToArray();

        Assert.Contains("between 2 and 8", await new AskPlugin(person).UserAsync("Which?", options));
        Assert.Null(person.Asked);
    }

    [Fact]
    public async Task RejectsAnEmptyQuestion()
        => Assert.Contains("question is empty", await new AskPlugin(new Person("A")).UserAsync(" ", ["A", "B"]));
}
