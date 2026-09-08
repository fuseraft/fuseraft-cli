using fuseraft.Core;
using fuseraft.Infrastructure.Storage;

namespace FuseraftCli.Tests;

/// <summary>
/// Pins the one-time, transparent migration <see cref="UserConfigStore.Load"/> performs the
/// first time it reads a pre-sectioning flat config, or finds a standalone
/// <c>repl-mcp-servers.json</c> left over from before MCP servers were folded into the same
/// file. See <see cref="UserConfigStoreLegacyKeyFileTests"/> for the sibling plaintext-.key
/// migration this mirrors.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class UserConfigStoreMigrationTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public UserConfigStoreMigrationTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    [Fact]
    public void Load_WithLegacyFlatConfig_MigratesFieldsAndRewritesFile()
    {
        Directory.CreateDirectory(FuseraftPaths.GlobalRoot);
        File.WriteAllText(FuseraftPaths.GlobalConfig, """
            {
              "modelId": "claude-sonnet-4-6",
              "endpoint": "https://api.anthropic.com",
              "provider": "anthropic",
              "apiKeyEnvVar": "ANTHROPIC_API_KEY",
              "replContextBudget": 12345
            }
            """);

        var (config, _) = UserConfigStore.Load();

        Assert.NotNull(config);
        Assert.Equal("claude-sonnet-4-6",          config!.ModelId);
        Assert.Equal("https://api.anthropic.com",  config.Endpoint);
        Assert.Equal("anthropic",                  config.Provider);
        Assert.Equal("ANTHROPIC_API_KEY",          config.ApiKeyEnvVar);
        Assert.Equal(12345,                        config.Repl.ContextBudget);

        // The migration is self-healing: the next Load() should see the new nested shape,
        // not re-run the legacy branch.
        var rewritten = File.ReadAllText(FuseraftPaths.GlobalConfig);
        Assert.Contains("\"provider\": {", rewritten);
        Assert.DoesNotContain("\"replContextBudget\"", rewritten);
    }

    [Fact]
    public void Load_WithLegacyMcpServersFile_FoldsInAndDeletesOldFile()
    {
        // In practice a repl-mcp-servers.json never exists without a config file alongside it
        // — /mcp add is only reachable from inside a REPL session, which requires the setup
        // wizard (and therefore a config file) to run first. Write a minimal already-configured
        // config so this test reflects that real upgrade path.
        Directory.CreateDirectory(FuseraftPaths.GlobalRoot);
        File.WriteAllText(FuseraftPaths.GlobalConfig, """{"provider":{"modelId":"claude-sonnet-4-6"}}""");
        var legacyMcpPath = Path.Combine(FuseraftPaths.GlobalRoot, "repl-mcp-servers.json");
        File.WriteAllText(legacyMcpPath, """[{"Name":"filesystem","Transport":"stdio","Command":"npx"}]""");

        var (config, _) = UserConfigStore.Load();

        Assert.NotNull(config);
        Assert.Single(config!.McpServers);
        Assert.Equal("filesystem", config.McpServers[0].Name);
        Assert.False(File.Exists(legacyMcpPath));
    }
}
