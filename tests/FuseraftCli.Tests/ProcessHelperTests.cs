using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="ProcessHelper.ExpandEnvTokens"/>.
/// These cover the env-var expansion used by <see cref="fuseraft.Cli.OrchestratorBuilder"/>
/// when resolving <c>${VAR}</c> tokens in <c>ApiProfiles</c> and <c>HttpAllowedHosts</c>.
/// </summary>
public sealed class ProcessHelperTests
{
    // Unique prefix prevents collisions with real env vars in the test runner's environment.
    private const string Prefix = "FUSERAFT_TEST_";

    // -----------------------------------------------------------------------
    // ExpandEnvTokens
    // -----------------------------------------------------------------------

    [Fact]
    public void ExpandEnvTokens_NoTokens_ReturnsInputUnchanged()
    {
        Assert.Equal("plain string", ProcessHelper.ExpandEnvTokens("plain string"));
    }

    [Fact]
    public void ExpandEnvTokens_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ProcessHelper.ExpandEnvTokens(string.Empty));
    }

    [Fact]
    public void ExpandEnvTokens_SingleToken_ReplacedWithEnvVarValue()
    {
        var varName = Prefix + "SINGLE";
        Environment.SetEnvironmentVariable(varName, "mycompany.service-now.com");
        try
        {
            var result = ProcessHelper.ExpandEnvTokens($"https://${{{varName}}}/api/now");
            Assert.Equal("https://mycompany.service-now.com/api/now", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ExpandEnvTokens_MultipleTokens_AllReplaced()
    {
        var hostVar  = Prefix + "HOST";
        var tokenVar = Prefix + "TOKEN";
        Environment.SetEnvironmentVariable(hostVar,  "snow.example.com");
        Environment.SetEnvironmentVariable(tokenVar, "secret123");
        try
        {
            var input  = $"https://${{{hostVar}}}/api Authorization:Bearer ${{{tokenVar}}}";
            var result = ProcessHelper.ExpandEnvTokens(input);
            Assert.Equal("https://snow.example.com/api Authorization:Bearer secret123", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar,  null);
            Environment.SetEnvironmentVariable(tokenVar, null);
        }
    }

    [Fact]
    public void ExpandEnvTokens_UnsetVariable_ReplacedWithEmptyString()
    {
        // Guarantee the variable is not set.
        var varName = Prefix + "DEFINITELY_NOT_SET";
        Environment.SetEnvironmentVariable(varName, null);

        var result = ProcessHelper.ExpandEnvTokens($"prefix-${{{varName}}}-suffix");
        Assert.Equal("prefix--suffix", result);
    }

    [Fact]
    public void ExpandEnvTokens_AdjacentTokens_BothExpanded()
    {
        var aVar = Prefix + "A";
        var bVar = Prefix + "B";
        Environment.SetEnvironmentVariable(aVar, "hello");
        Environment.SetEnvironmentVariable(bVar, "world");
        try
        {
            var result = ProcessHelper.ExpandEnvTokens($"${{{aVar}}}${{{bVar}}}");
            Assert.Equal("helloworld", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(aVar, null);
            Environment.SetEnvironmentVariable(bVar, null);
        }
    }

    [Fact]
    public void ExpandEnvTokens_NoDollarBrace_ReturnsInputUnchanged()
    {
        // Strings with $ but no ${ should not be touched.
        Assert.Equal("cost is $10", ProcessHelper.ExpandEnvTokens("cost is $10"));
    }

    [Fact]
    public void ExpandEnvTokens_TokenInHeaderValue_Expanded()
    {
        var varName = Prefix + "AUTH";
        Environment.SetEnvironmentVariable(varName, "Basic dXNlcjpwYXNz");
        try
        {
            var result = ProcessHelper.ExpandEnvTokens($"${{{varName}}}");
            Assert.Equal("Basic dXNlcjpwYXNz", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }
}
