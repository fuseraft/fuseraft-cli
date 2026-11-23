using System.Diagnostics;

namespace fuseraft.Infrastructure.KeyStore;

// macOS: Keychain via the security CLI.
internal sealed class MacOsKeychainStore : IApiKeyStore
{
    private const string Service = "fuseraft-cli";

    public string StoreName => "macOS Keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public async Task<string?> RetrieveAsync(string account = "default")
    {
        var (exit, stdout, _) = await RunAsync(
            "find-generic-password", "-s", Service, "-a", account, "-w");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    public async Task StoreAsync(string apiKey, string account = "default")
    {
        // -U updates if the entry already exists.
        var (exit, _, stderr) = await RunAsync(
            "add-generic-password", "-s", Service, "-a", account, "-w", apiKey, "-U");
        if (exit != 0)
            throw new InvalidOperationException($"security store failed: {stderr.Trim()}");
    }

    public async Task DeleteAsync(string account = "default")
    {
        await RunAsync("delete-generic-password", "-s", Service, "-a", account);
    }

    public async Task<IReadOnlyList<string>> ListAccountsAsync()
    {
        // Without -d this prints attributes only: no secrets, and no keychain password prompt.
        var (exit, stdout, stderr) = await RunAsync("dump-keychain");
        if (exit != 0) throw new InvalidOperationException($"security dump-keychain failed: {stderr.Trim()}");
        return ParseDumpAccounts(stdout, Service);
    }

    /// <summary>The <c>acct</c> of every generic-password item whose <c>svce</c> is <paramref name="service"/> in <c>security dump-keychain</c> output.</summary>
    internal static IReadOnlyList<string> ParseDumpAccounts(string dump, string service)
    {
        var accounts = new List<string>();
        string? account = null, itemService = null;
        var generic = false;

        void Flush()
        {
            if (generic && itemService == service && !string.IsNullOrEmpty(account) && !accounts.Contains(account))
                accounts.Add(account);
            account = itemService = null;
            generic = false;
        }

        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("keychain: ", StringComparison.Ordinal)) { Flush(); continue; }
            if (line == "class: \"genp\"") generic = true;
            else if (line.StartsWith("\"acct\"<blob>=", StringComparison.Ordinal)) account = Quoted(line);
            else if (line.StartsWith("\"svce\"<blob>=", StringComparison.Ordinal)) itemService = Quoted(line);
        }
        Flush();
        return accounts;

        // `"acct"<blob>="value"`; a value with non-printable bytes is hex instead, and isn't one fuseraft wrote.
        static string? Quoted(string line)
        {
            var eq = line.IndexOf("=\"", StringComparison.Ordinal);
            return eq < 0 || !line.EndsWith('"') ? null : line[(eq + 2)..^1];
        }
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
