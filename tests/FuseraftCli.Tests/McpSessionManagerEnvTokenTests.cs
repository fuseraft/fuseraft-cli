using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

public sealed class McpSessionManagerEnvTokenTests
{
    [Fact]
    public void UnsetEnvTokens_NamesEachUnsetVariableWithItsSetting()
    {
        var set   = "FUSERAFT_TEST_SET_" + Guid.NewGuid().ToString("N");
        var unset = "FUSERAFT_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(set, "x");
        try
        {
            var server = new McpServerConfig
            {
                Name    = "db",
                Env     = new() { ["PASSWORD"] = $"${{{unset}}}", ["USER"] = $"${{{set}}}", ["CLEARED"] = null },
                Headers = new() { ["Authorization"] = $"Bearer ${{{unset}}}" },
                Url     = $"https://example.test/${{{set}}}",
            };

            Assert.Equal([("Env PASSWORD", unset), ("header Authorization", unset)], McpSessionManager.UnsetEnvTokens(server));
        }
        finally
        {
            Environment.SetEnvironmentVariable(set, null);
        }
    }
}
