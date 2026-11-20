using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.Storage;
using Repl = fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")]
public sealed class ModelLimitStoreTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    // A model ID no other test uses, so a limit learned here can't change another test's budget.
    private readonly string _model = $"limit-test-{Guid.NewGuid():N}";
    private int _lookups;

    public ModelLimitStoreTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        ModelLimitStore.Disable();
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private void Enable(ModelInputLimit? answer) => ModelLimitStore.Enable(
        (config, _) =>
        {
            Interlocked.Increment(ref _lookups);
            return Task.FromResult(config.ModelId == _model ? answer : null);
        },
        _ => "https://llm.example.com/v1");

    private ModelConfig Config => new() { ModelId = _model };

    [Fact]
    public void Off_UntilEnabled()
    {
        Assert.Null(ModelLimitStore.Learn(Config));
        Assert.Equal(0, _lookups);
    }

    [Fact]
    public void LearnedLimit_SetsTheReplBudget()
    {
        Enable(new ModelInputLimit(450_000, "90% of context_length from the provider's model list"));

        ModelLimitStore.Learn(Config);

        Assert.Equal((450_000, "90% of context_length from the provider's model list"), Repl.ModelContextWindow.Describe(_model));
        // repl.contextBudget still wins.
        Assert.Equal((200_000, "repl.contextBudget"), Repl.ModelContextWindow.Describe(_model, 200_000));
    }

    [Fact]
    public void UnknownLimit_FallsBackToTheNameEstimate()
    {
        Enable(null);

        Assert.Null(ModelLimitStore.Learn(Config));
        Assert.Equal("estimated from the model name", Repl.ModelContextWindow.Describe(_model).Source);
    }

    [Fact]
    public void DiskCache_SparesTheNextSessionALookup()
    {
        Enable(new ModelInputLimit(500_000, "max_input_tokens from the LiteLLM gateway"));
        ModelLimitStore.Learn(Config);
        Assert.True(File.Exists(ModelLimitStore.CachePath));

        ModelLimitStore.Disable();   // a new session: nothing in memory
        Enable(null);
        var limit = ModelLimitStore.Learn(Config);

        Assert.Equal(1, _lookups);
        Assert.Equal(500_000, limit!.Tokens);
    }

    [Fact]
    public void UnknownLimit_IsRememberedToo()
    {
        Enable(null);
        ModelLimitStore.Learn(Config);
        ModelLimitStore.Disable();
        Enable(null);

        ModelLimitStore.Learn(Config);

        Assert.Equal(1, _lookups);
    }
}
