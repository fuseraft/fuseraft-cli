using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

public sealed class SecretToolKeyStoreTests
{
    [Fact]
    public void ParseSearchAccounts_ReadsAccountLinesOnly_NeverTheSecret()
    {
        // secret-tool search prints each item's path, label and secret on stdout and its attributes on stderr.
        const string output = """
            [/org/freedesktop/secrets/collection/login/17]
            label = fuseraft API key
            secret = sk-live-123
            created = 2026-11-01 10:00:00
            [/org/freedesktop/secrets/collection/login/10]
            label = fuseraft API key
            secret = {"access_token":"t"}

            attribute.account = provider:https://api.x.ai:443
            attribute.service = fuseraft-cli
            attribute.account = mcp-oauth-LiveOAuth-1fcb1e31
            attribute.service = fuseraft-cli
            """;

        var accounts = SecretToolKeyStore.ParseSearchAccounts(output);

        Assert.Equal(["provider:https://api.x.ai:443", "mcp-oauth-LiveOAuth-1fcb1e31"], accounts);
        Assert.DoesNotContain(accounts, a => a.Contains("sk-live"));
    }

    [Fact]
    public void ParseSearchAccounts_IsEmptyWhenNothingMatched() =>
        Assert.Empty(SecretToolKeyStore.ParseSearchAccounts(""));
}
