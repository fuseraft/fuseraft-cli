using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary><c>/model</c> and <c>/switch</c>: a key never reaches a server it wasn't meant for.</summary>
public sealed class ReplFactoryModelSwitchTests : IDisposable
{
    private readonly ChatClientFactory _factory = new();
    private readonly FakeKeyStore _keys = new();

    public void Dispose() => _factory.Dispose();

    private Task<ModelConfig> SwitchAsync(ModelConfig current, string model, string? effort = null) =>
        ReplFactory.SwitchModelConfigAsync(_factory, _keys, current, model, effort);

    [Fact]
    public async Task Gateway_StaysOnItsServer_WithItsKey_ForAnotherVendorsModel()
    {
        var gateway = new ModelConfig { ModelId = "grok-4", Provider = "openai", Endpoint = "https://llm.example/v1", ApiKeyEnvVar = "GATEWAY_KEY" };

        var next = _factory.Resolve(await SwitchAsync(gateway, "claude-sonnet-4-6", "high"));

        Assert.Equal(("claude-sonnet-4-6", "high"), (next.ModelId, next.ReasoningEffort));
        Assert.Equal(("openai", "https://llm.example/v1", "GATEWAY_KEY"), (next.Provider, next.Endpoint, next.ApiKeyEnvVar));
    }

    [Fact]
    public async Task KeylessGateway_NeverPicksUpTheVendorsKeyVariable()
    {
        var local = new ModelConfig { ModelId = "fake-model", Provider = "openai", Endpoint = "http://127.0.0.1:8765/v1" };

        var next = _factory.Resolve(await SwitchAsync(local, "claude-sonnet-4-6"));

        Assert.Equal("http://127.0.0.1:8765/v1", next.Endpoint);
        Assert.Equal(string.Empty, next.ApiKeyEnvVar);
    }

    [Fact]
    public async Task HostInjectedKey_StaysWithItsServer()
    {
        // FUSERAFT_API_KEY from VS Code or fuseraft-web: a key that doesn't say whose server it's for.
        var host = new ModelConfig { ModelId = "claude-sonnet-4-6", ApiKey = "sk-ant-host" };

        var next = await SwitchAsync(host, "gpt-4o");

        Assert.Equal("https://api.anthropic.com", next.Endpoint);
        Assert.Equal("sk-ant-host", next.ApiKey);
    }

    [Fact]
    public async Task NamedProvider_StaysOnItsServer()
    {
        var anthropic = new ModelConfig { ModelId = "claude-sonnet-4-6", Provider = "anthropic", ApiKey = "sk-ant", ApiKeyOrigin = "https://api.anthropic.com" };

        var next = await SwitchAsync(anthropic, "gpt-4o");

        Assert.Equal(("anthropic", "https://api.anthropic.com"), (next.Provider, next.Endpoint));
    }

    [Fact]
    public async Task GuessedConnection_MovesToTheNewProvider_WithThatProvidersOwnKey()
    {
        await ProviderKeys.StoreAsync(_keys, "https://api.openai.com/v1", "sk-openai-saved");
        var guessed = new ModelConfig { ModelId = "claude-sonnet-4-6", ApiKey = "sk-ant", ApiKeyOrigin = "https://api.anthropic.com" };

        var next = _factory.Resolve(await SwitchAsync(guessed, "gpt-4o"));

        Assert.Equal("https://api.openai.com/v1", next.Endpoint);
        Assert.Equal("sk-openai-saved", next.ApiKey);
        Assert.Equal(ProviderKeys.Origin("https://api.openai.com/v1"), next.ApiKeyOrigin);
    }

    [Fact]
    public async Task GuessedConnection_WithNoSavedKey_UsesOnlyTheNewProvidersVariable()
    {
        var guessed = new ModelConfig { ModelId = "claude-sonnet-4-6", ApiKey = "sk-ant", ApiKeyOrigin = "https://api.anthropic.com" };

        var next = _factory.Resolve(await SwitchAsync(guessed, "gpt-4o"));

        Assert.Equal(string.Empty, next.ApiKey);
        Assert.Equal("OPENAI_API_KEY", next.ApiKeyEnvVar);
    }

    [Fact]
    public async Task SameProvider_KeepsTheConnectionUnpinned_SoALaterSwitchCanStillMove()
    {
        var guessed = new ModelConfig { ModelId = "claude-sonnet-4-6", ApiKey = "sk-ant", ApiKeyOrigin = "https://api.anthropic.com" };

        var next = await SwitchAsync(guessed, "claude-opus-4-1");

        Assert.Equal(string.Empty, next.Endpoint);
        Assert.Equal("sk-ant", next.ApiKey);
    }

    private sealed class FakeKeyStore : IApiKeyStore
    {
        private readonly Dictionary<string, string> _entries = [];
        public string StoreName => "fake";
        public bool IsAvailable => true;
        public Task<string?> RetrieveAsync(string account = "default") => Task.FromResult(_entries.GetValueOrDefault(account));
        public Task StoreAsync(string apiKey, string account = "default") { _entries[account] = apiKey; return Task.CompletedTask; }
        public Task DeleteAsync(string account = "default") { _entries.Remove(account); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListAccountsAsync() => Task.FromResult<IReadOnlyList<string>>([.. _entries.Keys]);
    }
}
