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

    [Fact]
    public void MaskResult_ObjectHoldingASecret_BecomesItsMaskedJson()
    {
        const string name = "FUSERAFT_TEST_MASKRESULT_TOKEN";
        const string value = "tok-test-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var masked = Assert.IsType<System.Text.Json.JsonElement>(EnvSecretMasker.MaskResult(new { output = $"env: {value}" }));

            Assert.Equal($"env: {EnvSecretMasker.Placeholder}", masked.GetProperty("output").GetString());
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void MaskResult_JsonString_StaysAJsonStringWithoutExtraQuotes()
    {
        const string name = "FUSERAFT_TEST_MASKRESULT_ELEMENT_TOKEN";
        const string value = "tok-test-element-0123456789";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var element = System.Text.Json.JsonSerializer.SerializeToElement($"KEY={value}");

            var masked = Assert.IsType<System.Text.Json.JsonElement>(EnvSecretMasker.MaskResult(element));

            Assert.Equal($"KEY={EnvSecretMasker.Placeholder}", masked.GetString());
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void MaskResult_ObjectWithoutASecret_IsReturnedAsIs()
    {
        var result = new { output = "nothing to hide here" };

        Assert.Same(result, EnvSecretMasker.MaskResult(result));
    }

    [Fact]
    public void SecretNamesIn_NamesTheVariable_NeverTheValue()
    {
        const string name = "FUSERAFT_TEST_NAMES_API_KEY";
        const string value = "key-test-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var names = EnvSecretMasker.SecretNamesIn($"KEY={value}");

            Assert.Contains(name, names);
            Assert.DoesNotContain(names, n => n.Contains(value));
            Assert.True(EnvSecretMasker.ContainsSecret(value));
            Assert.False(EnvSecretMasker.ContainsSecret("no secrets in this text"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
