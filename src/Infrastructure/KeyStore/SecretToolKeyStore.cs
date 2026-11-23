using System.Diagnostics;

namespace fuseraft.Infrastructure.KeyStore;

// Linux: GNOME Keyring / libsecret via the secret-tool CLI.
internal sealed class SecretToolKeyStore(string service = "fuseraft-cli") : IApiKeyStore
{
    private readonly string _service = service;

    public string StoreName => "GNOME Keyring (secret-tool)";

    public bool IsAvailable => _available ??= CheckAvailable();
    private bool? _available;

    public async Task<string?> RetrieveAsync(string account = "default")
    {
        var (exit, stdout, _) = await RunAsync("lookup", "service", _service, "account", account);
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    public async Task StoreAsync(string apiKey, string account = "default")
    {
        var psi = new ProcessStartInfo("secret-tool", $"store --label \"fuseraft API key\" service {_service} account {account}")
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

    public async Task DeleteAsync(string account = "default")
    {
        await RunAsync("clear", "service", _service, "account", account);
    }

    public async Task<IReadOnlyList<string>> ListAccountsAsync()
    {
        // --unlock so entries in a locked keyring are listed too, not silently missed.
        var (exit, stdout, stderr) = await RunAsync("search", "--all", "--unlock", "service", _service);
        if (exit != 0) throw new InvalidOperationException($"secret-tool search failed: {stderr.Trim()}");
        return ParseSearchAccounts(stdout + "\n" + stderr);
    }

    /// <summary>
    /// The <c>attribute.account</c> values in <c>secret-tool search</c> output. It prints attributes on stderr and each
    /// secret on stdout, so both streams are passed in; only account lines are read.
    /// </summary>
    internal static IReadOnlyList<string> ParseSearchAccounts(string output)
    {
        const string Prefix = "attribute.account = ";
        return output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(l => l[Prefix.Length..])
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
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
