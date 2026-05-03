namespace fuseraft.Infrastructure.KeyStore;

public static class ApiKeyStoreFactory
{
    public static IApiKeyStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialManagerStore();

        if (OperatingSystem.IsMacOS())
            return new MacOsKeychainStore();

        if (OperatingSystem.IsLinux())
        {
            var store = new SecretToolKeyStore();
            if (store.IsAvailable) return store;
        }

        return new PlainTextFallbackKeyStore();
    }
}
