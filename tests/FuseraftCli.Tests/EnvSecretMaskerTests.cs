using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class EnvSecretMaskerTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AWS_SESSION_TOKEN")]
    [InlineData("PGPASSWORD")]
    [InlineData("DB_PASSWORD")]
    [InlineData("MYSQL_PWD")]
    [InlineData("STRIPE_SECRET_KEY")]
    [InlineData("NPM_TOKEN")]
    [InlineData("client_secret")]
    [InlineData("MY_KEY")]
    [InlineData("SERVICE_CREDENTIALS")]
    public void IsSensitiveName_SecretLookingNames_AreSensitive(string name) =>
        Assert.True(EnvSecretMasker.IsSensitiveName(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("PWD")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("KEYBOARD_LAYOUT")]
    [InlineData("GNOME_KEYRING_CONTROL")]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("GITHUB_TOKEN_FILE")]
    [InlineData("SSH_KEY_PATH")]
    [InlineData("TOKEN_URL")]
    [InlineData("MONKEY")]
    [InlineData("")]
    public void IsSensitiveName_OrdinaryOrPointerNames_AreNotSensitive(string name) =>
        Assert.False(EnvSecretMasker.IsSensitiveName(name));

    [Fact]
    public void Mask_ReplacesSecretValueAnywhereItAppears()
    {
        const string name = "FUSERAFT_TEST_MASK_API_KEY";
        const string value = "sk-test-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var masked = EnvSecretMasker.Mask($"header: {value}\nagain {value}!");

            Assert.DoesNotContain(value, masked);
            Assert.Equal($"header: {EnvSecretMasker.Placeholder}\nagain {EnvSecretMasker.Placeholder}!", masked);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Mask_LeavesShortValuesAlone()
    {
        const string name = "FUSERAFT_TEST_MASK_SHORT_TOKEN";
        Environment.SetEnvironmentVariable(name, "true");
        try
        {
            Assert.Equal("it is true that", EnvSecretMasker.Mask("it is true that"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Mask_IgnoresValuesOfNonSecretNames()
    {
        const string name = "FUSERAFT_TEST_MASK_PLAIN_SETTING";
        const string value = "plain-value-12345";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            Assert.Equal($"x {value} y", EnvSecretMasker.Mask($"x {value} y"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Mask_PrefersLongestValue_SoNoTailOfALongerSecretSurvives()
    {
        const string shortName = "FUSERAFT_TEST_MASK_A_TOKEN";
        const string longName  = "FUSERAFT_TEST_MASK_B_TOKEN";
        const string shortVal  = "abcdefgh1234";
        const string longVal   = "abcdefgh1234-extra-suffix";
        Environment.SetEnvironmentVariable(shortName, shortVal);
        Environment.SetEnvironmentVariable(longName, longVal);
        try
        {
            var masked = EnvSecretMasker.Mask($"tok={longVal}");

            Assert.Equal($"tok={EnvSecretMasker.Placeholder}", masked);
        }
        finally
        {
            Environment.SetEnvironmentVariable(shortName, null);
            Environment.SetEnvironmentVariable(longName, null);
        }
    }

    [Fact]
    public void Mask_PicksUpVariablesSetAfterFirstUse()
    {
        Assert.Equal("nothing here", EnvSecretMasker.Mask("nothing here"));

        const string name = "FUSERAFT_TEST_MASK_LATE_SECRET";
        const string value = "late-bound-secret-value";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            Assert.DoesNotContain(value, EnvSecretMasker.Mask($"got {value}"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mask_NullOrEmpty_ReturnsEmpty(string? text) =>
        Assert.Equal(string.Empty, EnvSecretMasker.Mask(text));
}
