using fuseraft.Core.Models;

namespace FuseraftCli.Tests;

public sealed class UserConfigTests
{
    [Fact]
    public void KeyVariable_ThatIsSet_CountsAsConfigured()
    {
        var name = $"FUSERAFT_TEST_KEY_{Guid.NewGuid():N}";
        var cfg  = new UserConfig { ModelId = "acme", Endpoint = "https://llm.example/v1", ApiKeyEnvVar = name };
        Assert.False(cfg.IsConfigured);

        Environment.SetEnvironmentVariable(name, "sk-test");
        try { Assert.True(cfg.IsConfigured); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
