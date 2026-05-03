using System.Diagnostics;

namespace fuseraft.Infrastructure.KeyStore;

// macOS: Keychain via the security CLI.
internal sealed class MacOsKeychainStore : IApiKeyStore
{
    private const string Service = "fuseraft-cli";
    private const string Account = "default";

    public string StoreName => "macOS Keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public async Task<string?> RetrieveAsync()
    {
        var (exit, stdout, _) = await RunAsync(
            "find-generic-password", "-s", Service, "-a", Account, "-w");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    public async Task StoreAsync(string apiKey)
    {
        // -U updates if the entry already exists.
        var (exit, _, stderr) = await RunAsync(
            "add-generic-password", "-s", Service, "-a", Account, "-w", apiKey, "-U");
        if (exit != 0)
            throw new InvalidOperationException($"security store failed: {stderr.Trim()}");
    }

    public async Task DeleteAsync()
    {
        await RunAsync("delete-generic-password", "-s", Service, "-a", Account);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("security", string.Join(' ', args.Select(a => $"\"{a}\"")))
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p   = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start security.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Read stdout and stderr concurrently to prevent pipe-buffer deadlocks.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
