using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

public sealed class MacOsKeychainStoreTests
{
    private const string Dump = """
        keychain: "/Users/u/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            0x00000007 <blob>="fuseraft-cli"
            0x00000008 <blob>=<NULL>
            "acct"<blob>="provider:https://api.anthropic.com:443"
            "cdat"<timedate>=0x32303236313130313130303030305A00  "20261101100000Z\000"
            "svce"<blob>="fuseraft-cli"
            "type"<uint32>=<NULL>
        keychain: "/Users/u/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            "acct"<blob>="default"
            "svce"<blob>="some-other-app"
        keychain: "/Users/u/Library/Keychains/login.keychain-db"
        version: 512
        class: "inet"
        attributes:
            "acct"<blob>="web-login"
            "svce"<blob>="fuseraft-cli"
        keychain: "/Users/u/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            "acct"<blob>=0xE2988300  "\342\230\203\000"
            "svce"<blob>="fuseraft-cli"
        keychain: "/Users/u/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            "acct"<blob>="mcp-oauth-Docs-a1b2c3d4"
            "svce"<blob>="fuseraft-cli"
        """;

    [Fact]
    public void ParseDumpAccounts_ReadsGenericPasswordsForTheService()
    {
        Assert.Equal(["provider:https://api.anthropic.com:443", "mcp-oauth-Docs-a1b2c3d4"],
            MacOsKeychainStore.ParseDumpAccounts(Dump, "fuseraft-cli"));
    }

    [Fact]
    public void ParseDumpAccounts_IsEmptyForAServiceWithNoItems() =>
        Assert.Empty(MacOsKeychainStore.ParseDumpAccounts(Dump, "nothing-here"));
}
