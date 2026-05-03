using System.Runtime.InteropServices;
using System.Text;

namespace fuseraft.Infrastructure.KeyStore;

// Last-resort fallback: stores the key in ~/.fuseraft/.key with mode 600 on Unix.
// Prints a warning so users know this is not as secure as a native keychain.
internal sealed class PlainTextFallbackKeyStore : IApiKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".fuseraft", ".key");

    public string StoreName => "plain-text file (~/.fuseraft/.key)";

    public bool IsAvailable => true;

    public Task<string?> RetrieveAsync()
    {
        if (!File.Exists(KeyPath)) return Task.FromResult<string?>(null);
        try { return Task.FromResult<string?>(File.ReadAllText(KeyPath, Encoding.UTF8).Trim()); }
        catch { return Task.FromResult<string?>(null); }
    }

    public Task StoreAsync(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllText(KeyPath, apiKey, Encoding.UTF8);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return Task.CompletedTask;
    }

    public Task DeleteAsync()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
        return Task.CompletedTask;
    }
}
