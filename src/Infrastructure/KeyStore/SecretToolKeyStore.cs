using System.Diagnostics;

namespace fuseraft.Infrastructure.KeyStore;

// Linux: GNOME Keyring / libsecret via the secret-tool CLI.
internal sealed class SecretToolKeyStore : IApiKeyStore
{
    private const string Service = "fuseraft-cli";
    private const string Account = "default";

    public string StoreName => "GNOME Keyring (secret-tool)";

    public bool IsAvailable => _available ??= CheckAvailable();
    private bool? _available;

    public async Task<string?> RetrieveAsync()
    {
        var (exit, stdout, _) = await RunAsync("lookup", "service", Service, "account", Account);
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    public async Task StoreAsync(string apiKey)
    {
        var psi = new ProcessStartInfo("secret-tool", $"store --label \"fuseraft API key\" service {Service} account {Account}")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p   = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start secret-tool.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.StandardInput.WriteAsync(apiKey);
        p.StandardInput.Close();
        // Read stderr concurrently so the pipe buffer never fills while we wait for exit.
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"secret-tool store failed: {(await stderrTask).Trim()}");
    }

    public async Task DeleteAsync()
    {
        await RunAsync("clear", "service", Service, "account", Account);
    }

    private static bool CheckAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("secret-tool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            return p?.WaitForExit(3000) == true;
        }
        catch { return false; }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("secret-tool", string.Join(' ', args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p   = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start secret-tool.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Read stdout and stderr concurrently to prevent pipe-buffer deadlocks.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
