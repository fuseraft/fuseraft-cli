namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Shared utility for running child processes and capturing their output.
/// Used by ShellPlugin, GitPlugin, and other process-based plugins.
/// </summary>
internal static class ProcessHelper
{
    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/>, captures
    /// stdout + stderr, and returns a <see cref="ProcessResult"/>.
    /// </summary>
    internal static async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 60,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(executable, arguments, null, workingDirectory, timeoutSeconds, extraEnvironment, cancellationToken);

    /// <summary>
    /// Runs <paramref name="executable"/> with each element of <paramref name="argumentList"/>
    /// passed as a separate argument. Prefer this overload over the string form when any
    /// argument comes from untrusted input — it bypasses shell quoting entirely.
    /// </summary>
    internal static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> argumentList,
        string? workingDirectory = null,
        int timeoutSeconds = 60,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(executable, null, argumentList, workingDirectory, timeoutSeconds, extraEnvironment, cancellationToken);

    private static async Task<ProcessResult> RunCoreAsync(
        string executable,
        string? arguments,
        IEnumerable<string>? argumentList,
        string? workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = ResolveWorkDir(workingDirectory),
            RedirectStandardInput  = true,   // closed immediately — prevents child processes from
            RedirectStandardOutput = true,   // inheriting the terminal and blocking on user input
            RedirectStandardError  = true,   // (e.g. git credential helpers, interactive pagers)
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (argumentList is not null)
        {
            foreach (var arg in argumentList)
                startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.Arguments = arguments ?? string.Empty;
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
                process.StartInfo.Environment[key] = value;
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(string.Empty, PluginResult.Error($"Failed to start process '{executable}': {ex.Message}"), -1);
        }

        // Close stdin immediately. Children that try to read from it get EOF instead of
        // inheriting the terminal and blocking indefinitely (git credential helpers, pagers).
        process.StandardInput.Close();

        // Read stdout and stderr concurrently to avoid buffer-deadlocks.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        static async Task KillAndDrainAsync(System.Diagnostics.Process p, Task<string> o, Task<string> e, int timeoutSeconds)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            // Give the readers a short window to drain after the kill, then abandon them.
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await o.WaitAsync(drainCts.Token).ConfigureAwait(false); } catch { }
            try { await e.WaitAsync(drainCts.Token).ConfigureAwait(false); } catch { }
        }

        try
        {
            await process.WaitForExitAsync(cts.Token);

            // Happy path: process exited within timeout. Still guard the drain — a child
            // process might have inherited the pipe and be holding it open even though the
            // main process has exited.
            var drainTimeout = Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            var readBoth = Task.WhenAll(stdoutTask, stderrTask);
            if (await Task.WhenAny(readBoth, drainTimeout) == drainTimeout)
            {
                // Something is still holding the pipe open; kill the process tree and bail.
                await KillAndDrainAsync(process, stdoutTask, stderrTask, timeoutSeconds);
                return new ProcessResult(string.Empty, PluginResult.Timeout($"Process exceeded {timeoutSeconds}s limit."), -1);
            }

            return new ProcessResult(await stdoutTask, await stderrTask, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask, timeoutSeconds);
            return new ProcessResult(string.Empty, PluginResult.Timeout($"Process exceeded {timeoutSeconds}s limit."), -1);
        }
    }

    /// <summary>
    /// Replaces all <c>${VAR_NAME}</c> tokens in <paramref name="value"/> with the
    /// corresponding environment variable values. Tokens that reference unset variables
    /// are replaced with an empty string (matching shell behaviour).
    /// Returns <paramref name="value"/> unchanged when it contains no tokens.
    /// </summary>
    internal static string ExpandEnvTokens(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${"))
            return value;

        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\$\{([^}]+)\}",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty);
    }

    /// <summary>
    /// Resolves the PowerShell executable to use on Windows. Prefers <c>pwsh</c> (PowerShell 7+),
    /// which supports the <c>&amp;&amp;</c>/<c>||</c> chaining operators agents commonly emit out of
    /// bash habit; falls back to Windows PowerShell 5.1 (<c>powershell.exe</c>), which ships in every
    /// supported Windows release, so this always resolves to something runnable.
    /// </summary>
    internal static readonly Lazy<string> WindowsPowerShellPath = new(() =>
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            string candidate;
            try { candidate = Path.Combine(dir, "pwsh.exe"); }
            catch { continue; } // malformed PATH entry
            if (File.Exists(candidate)) return candidate;
        }

        var system32Path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(system32Path) ? system32Path : "powershell";
    });

    /// <summary>
    /// Expands a leading <c>~</c> to the current user's home directory.
    /// Process.Start and Path.GetFullPath do not do this — only shells do.
    /// </summary>
    internal static string ExpandHome(string path)
    {
        if (path.StartsWith("~/") || path == "~")
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Length > 2 ? path[2..] : string.Empty);
        return path;
    }

    private static string ResolveWorkDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Directory.GetCurrentDirectory();
        return ExpandHome(path);
    }
}

/// <summary>
/// Result of a child process execution.
/// </summary>
internal readonly record struct ProcessResult(string Stdout, string Stderr, int ExitCode)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Formats the result for return to an agent: exit code + combined output.
    /// </summary>
    // Maximum combined output characters returned to the model per shell command.
    // Large read-oriented commands (sed -n on big files, grep over many matches) can
    // otherwise balloon the within-turn context.
    private const int MaxOutputChars        = 15_000;
    // Failure output cap: head shows the first errors; tail shows the final summary line
    // (e.g. "X failed, Y passed"). Middle section is elided with a char count so the agent
    // knows how much was omitted.
    private const int MaxFailureOutputChars = 20_000;
    private const int FailureHeadChars      = 14_000;
    private const int FailureTailChars      =  5_000;

    public string ToPluginOutput()
    {
        var stdout = Stdout.TrimEnd();
        var stderr = Stderr.TrimEnd();

        if (Succeeded)
        {
            // Always surface stderr even on success — some interpreters (e.g. kiwi, node)
            // print errors to stderr and exit 0, which would otherwise look like a clean run.
            if (string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr))
                return PluginResult.Ok("Command completed with no output.");

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stdout)) parts.Add(stdout);
            if (!string.IsNullOrEmpty(stderr)) parts.Add($"[stderr] {stderr}");
            var combined = string.Join("\n", parts);

            // Truncate successful output to keep within-turn context manageable.
            if (combined.Length > MaxOutputChars)
                combined = combined[..MaxOutputChars] +
                    $"\n\n[TRUNCATED — output exceeded {MaxOutputChars:N0} chars. " +
                    $"Use a more targeted command (e.g. sed -n 'N,Mp') to read a specific range.]";

            return combined;
        }

        // Failure output: cap with head+tail so both the first errors AND the final summary
        // (e.g. "3 failed, 47 passed") are always visible. Uncapped failure output from large
        // test suites is the primary driver of 600k+ input-token turns.
        var failParts = new List<string> { $"[EXIT {ExitCode}]" };
        if (!string.IsNullOrEmpty(stdout)) failParts.Add(stdout);
        if (!string.IsNullOrEmpty(stderr)) failParts.Add($"[stderr] {stderr}");
        var failOutput = string.Join("\n", failParts);

        if (failOutput.Length > MaxFailureOutputChars)
        {
            var head    = failOutput[..FailureHeadChars];
            var tail    = failOutput[^FailureTailChars..];
            var omitted = failOutput.Length - FailureHeadChars - FailureTailChars;
            failOutput  = head
                + $"\n\n[... {omitted:N0} chars omitted — fix the first errors above, or use grep/sed to inspect the full log ...]\n\n"
                + tail;
        }

        return failOutput;
    }
}

/// <summary>
/// Shared factory for the bracketed prefix strings returned by all plugins.
/// Centralises the format so that agents always see a consistent vocabulary
/// regardless of which plugin produced the message.
/// </summary>
internal static class PluginResult
{
    public static string Ok(string message) => $"[OK] {message}";

    /// <summary>
    /// Return an actionable usage or request error caused by the current tool call, such as
    /// invalid arguments, unsupported inputs, or missing session state the caller can fix.
    /// </summary>
    public static string Error(string message) => $"[ERROR] {message}";

    public static string Info(string message) => $"[INFO] {message}";
    public static string Denied(string message) => $"[DENIED] {message}";
    public static string NotFound(string message) => $"[NOT FOUND] {message}";
    public static string Timeout(string message) => $"[TIMEOUT] {message}";

    /// <summary>
    /// Return an environment or dependency failure where the request itself is valid but the
    /// runtime cannot complete it until an external prerequisite is fixed.
    /// </summary>
    public static string Fail(string message) => $"[FAIL] {message}";
}
