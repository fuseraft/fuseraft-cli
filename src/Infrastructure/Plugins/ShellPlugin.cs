using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents the ability to execute shell commands and scripts.
///
/// SECURITY NOTE: This plugin runs arbitrary commands. In production, restrict agents
/// to a sandbox directory and consider using a container runtime or a restricted shell.
///
/// When <paramref name="sandboxRoot"/> is provided, every command's working directory is
/// constrained to that root. Commands that omit a <c>workingDirectory</c> argument default
/// to the sandbox root rather than the process current directory.
/// </summary>
public sealed class ShellPlugin : IDisposable, ITurnResettable
{
    private static readonly string Shell     = OperatingSystem.IsWindows() ? "cmd"  : ResolveUnixShell();
    private static readonly string ShellFlag = OperatingSystem.IsWindows() ? "/c"   : "-c";

    // Resolve bash from common locations so this works on NixOS, Alpine, and other
    // non-FHS distros where /bin/bash may not exist. Falls back to /bin/bash as a
    // last resort so the error message at least names the expected path.
    private static string ResolveUnixShell()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash" })
            if (File.Exists(candidate)) return candidate;
        return "/bin/bash";
    }

    // Agents very commonly default to PowerShell syntax on Windows (Get-ChildItem, $env:,
    // Where-Object, ...) even though cmd.exe is the sandboxed default shell here. cmd.exe has
    // no notion of cmdlets, so it fails to resolve the leading token and always reports this
    // exact, well-known message. Detecting it lets us retry once via PowerShell instead of
    // handing the agent a failure it would just retry itself — saving a wasted tool call.
    private const string CmdUnrecognizedCommandMessage = "is not recognized as an internal or external command";

    private static bool IsCmdUnrecognizedCommand(string text) =>
        text.Contains(CmdUnrecognizedCommandMessage, StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeShellMismatch(ProcessResult result) =>
        !result.Succeeded &&
        (IsCmdUnrecognizedCommand(result.Stdout) || IsCmdUnrecognizedCommand(result.Stderr));

    // Windows-only: if cmd.exe couldn't resolve the command at all, retry it via PowerShell
    // before returning to the caller. Only the successful PowerShell result replaces the
    // original — if PowerShell also fails, the original cmd.exe failure is preserved since
    // it's no less informative and avoids conflating two unrelated error messages.
    private static async Task<ProcessResult> WithWindowsPowerShellFallbackAsync(
        ProcessResult primary, Func<Task<ProcessResult>> retryViaPowerShell)
    {
        if (!OperatingSystem.IsWindows() || !LooksLikeShellMismatch(primary))
            return primary;

        var retried = await retryViaPowerShell();
        return retried.Succeeded ? retried : primary;
    }

    private readonly string? _sandboxRoot;
    private readonly Func<string, Task<bool>>? _approveCommand;
    private readonly ShellPolicy? _shellPolicy;
    private readonly IEventSink? _eventSink;
    private readonly object _tempDirLock = new();
    private string? _sessionTempDir;

    // Per-turn command dedup: tracks only the most recently run command.
    // If the exact same command is called again with no other shell command in between,
    // the cached result is returned so the agent can act on the failure rather than
    // re-running an identical command in a tight loop.
    // Any other intervening shell_run clears the entry, so file changes made via shell
    // (cat >, tee, heredocs, etc.) are always reflected on the next verify run.
    private string? _lastRunKey;
    private string? _lastRunOutput;

    void ITurnResettable.BeginTurn()
    {
        _lastRunKey    = null;
        _lastRunOutput = null;
    }

    /// <summary>
    /// Clears the per-turn command cache so that the next shell_run call executes
    /// fresh even within the same turn. Called by FileSystemPlugin after a successful
    /// write_file or patch_file so verify commands pick up changes immediately.
    /// </summary>
    internal void InvalidateRunCache()
    {
        _lastRunKey    = null;
        _lastRunOutput = null;
    }

    // Background job registry
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BackgroundJob> _jobs = new();

    private sealed class BackgroundJob(string jobId)
    {
        public string JobId { get; } = jobId;
        public System.Diagnostics.Process? Process { get; set; }
        public readonly System.Text.StringBuilder Output = new();
        public readonly object OutputLock = new();
        public Task? ReaderTask { get; set; }
        public bool Killed { get; set; }

        public bool IsRunning  => Process is not null && !Process.HasExited && !Killed;
        public int? ExitCode   => Process?.HasExited == true ? Process.ExitCode : null;

        private const int MaxOutputBytes = 100_000;

        public void AppendOutput(string chunk)
        {
            lock (OutputLock)
            {
                if (Output.Length + chunk.Length > MaxOutputBytes)
                {
                    var keep = MaxOutputBytes / 2;
                    Output.Remove(0, Output.Length - keep);
                    Output.Insert(0, "[… earlier output trimmed …]\n");
                }
                Output.Append(chunk);
            }
        }

        public string ReadOutput()
        {
            lock (OutputLock) return Output.ToString();
        }

        public void ClearOutput()
        {
            lock (OutputLock) Output.Clear();
        }
    }

    // Starts a redirected child process. Throws on failure — caller decides how to report it.
    private static System.Diagnostics.Process StartProcess(string exe, IEnumerable<string> args, string workingDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = exe,
            WorkingDirectory       = workingDirectory,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        return process;
    }

    // Attaches a job to a started process and begins draining its stdout/stderr into the
    // job's output buffer. Reading only starts here, so callers that need to discard output
    // from a previous attempt (see the PowerShell retry below) can safely clear it first.
    private static void WireOutputReaders(BackgroundJob job, System.Diagnostics.Process process)
    {
        job.Process = process;
        job.ReaderTask = Task.WhenAll(
            Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                        job.AppendOutput(line + "\n");
                }
                catch { /* process may have exited */ }
            }),
            Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) is not null)
                        job.AppendOutput($"[stderr] {line}\n");
                }
                catch { /* process may have exited */ }
            }));
    }

    // Background commands that turn out to be PowerShell syntax fail near-instantly under
    // cmd.exe with the same "not recognized" signature as the synchronous shell_run path.
    // Give the process a brief grace window to hit that failure; if it does, swap in a
    // PowerShell process before the job ID is ever handed back, so the agent never sees the
    // failed cmd.exe attempt. A command that's still running (or exited cleanly, or failed for
    // an unrelated reason) after the window is left alone.
    private static readonly TimeSpan BackgroundMismatchGracePeriod = TimeSpan.FromMilliseconds(400);

    private static async Task RetryBackgroundJobViaPowerShellIfMismatchedAsync(
        BackgroundJob job, System.Diagnostics.Process originalProcess, string command, string workingDirectory)
    {
        await Task.WhenAny(originalProcess.WaitForExitAsync(), Task.Delay(BackgroundMismatchGracePeriod));

        if (!originalProcess.HasExited || originalProcess.ExitCode == 0)
            return;

        if (!IsCmdUnrecognizedCommand(job.ReadOutput()))
            return;

        System.Diagnostics.Process retryProcess;
        try
        {
            retryProcess = StartProcess(
                ProcessHelper.WindowsPowerShellPath.Value,
                ["-NoProfile", "-NonInteractive", "-Command", command],
                workingDirectory);
        }
        catch
        {
            return; // PowerShell unavailable — leave the original cmd.exe failure visible
        }

        job.ClearOutput();
        WireOutputReaders(job, retryProcess);
        try { originalProcess.Dispose(); } catch { /* already exited */ }
    }

    public ShellPlugin(string? sandboxRoot = null, Func<string, Task<bool>>? approveCommand = null, ShellPolicy? shellPolicy = null, IEventSink? eventSink = null)
    {
        _sandboxRoot    = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : null;
        _approveCommand = approveCommand;
        _shellPolicy    = shellPolicy;
        _eventSink      = eventSink;
    }

    public void Dispose()
    {
        if (_sessionTempDir is not null && Directory.Exists(_sessionTempDir))
            try { Directory.Delete(_sessionTempDir, recursive: true); } catch { /* best effort */ }

        foreach (var job in _jobs.Values)
        {
            try { job.Process?.Kill(entireProcessTree: true); } catch { }
            try { job.Process?.Dispose(); } catch { }
        }
        _jobs.Clear();
    }

    // Core execution

    [Description("Run a shell command and return stdout/stderr. Pass quiet=true to get 'OK' on success instead of full output — cheaper when you only need to confirm success (e.g. scaffolding, 'dotnet restore', environment setup). Full output and exit code are always returned on failure regardless of quiet.")]
    public async Task<string> RunAsync(
        [Description("Shell command to execute.")] string command,
        [Description("Working directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 60,
        [Description("Return 'OK' instead of full output when the command succeeds.")] bool quiet = false)
    {
        // LLM outputs sometimes carry HTML entity encoding (e.g. &amp;&amp; instead of &&).
        // Decode before passing to the shell so commands execute as intended.
        command = System.Net.WebUtility.HtmlDecode(command);

        var sudoDenial = CheckForSudo(command);
        if (sudoDenial is not null) return sudoDenial;

        var policyDenial = CheckShellPolicy(command);
        if (policyDenial is not null) return policyDenial;

        if (_approveCommand is not null && !await _approveCommand(command))
            return PluginResult.Denied("Shell command blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        // Per-turn command dedup: if the exact same command was the last command run this
        // turn, return the cached output. Re-running an identical command back-to-back
        // almost always means the agent is looping — returning the cached result breaks
        // the loop and keeps the failure in context where the agent can act on it.
        // Any other intervening shell_run clears the cached entry so that file changes
        // made via shell (cat >, tee, heredocs, etc.) are reflected on the next verify run.
        // Applies regardless of quiet — the loop-detection concern is the same either way.
        var cacheKey = command.Trim() + "\0" + (resolvedDir ?? "(default)");
        if (_lastRunKey == cacheKey)
            return $"[Command already ran this turn — cached output follows]\n\n{_lastRunOutput}";

        var result = await ProcessHelper.RunAsync(
            Shell, [ShellFlag, command],
            resolvedDir, timeoutSeconds);

        result = await WithWindowsPowerShellFallbackAsync(result, () =>
            ProcessHelper.RunAsync(
                ProcessHelper.WindowsPowerShellPath.Value,
                ["-NoProfile", "-NonInteractive", "-Command", command],
                resolvedDir, timeoutSeconds));

        var output = result.ToPluginOutput();
        _lastRunKey    = cacheKey;
        _lastRunOutput = output;

        if (_eventSink is not null && IsBuildCommand(command))
        {
            var rawOutput  = result.Stdout + "\n" + result.Stderr;
            var commitHash = result.Succeeded ? await TryCaptureCommitHashAsync(resolvedDir) : null;
            _eventSink.Emit(new BuildResultEvent(
                Succeeded:  result.Succeeded,
                ExitCode:   result.ExitCode,
                Command:    command,
                CommitHash: commitHash,
                Errors:     ParseCompilerErrors(rawOutput))
            { Timestamp = DateTimeOffset.UtcNow });
        }

        return quiet && result.Succeeded ? "OK" : output;
    }

    private static async Task<string?> TryCaptureCommitHashAsync(string? workingDir)
    {
        try
        {
            var r = await ProcessHelper.RunAsync("git", ["rev-parse", "HEAD"], workingDir, 5);
            return r.Succeeded ? r.Stdout.Trim() : null;
        }
        catch { return null; }
    }

    private static readonly string[] BuildCommandPrefixes =
    [
        "dotnet build", "dotnet publish", "dotnet test",
        "cargo build",  "cargo test",     "cargo check",
        "go build",     "go test",        "go vet",
        "npm run build", "npm run test",  "npm test",
        "yarn build",   "yarn test",
        "python -m pytest", "pytest",
        "gradle build", "gradle test",
        "mvn package",  "mvn test",       "mvn compile",
        "cmake --build", "tsc",           "ng build",
    ];

    private static bool IsBuildCommand(string command)
    {
        var trimmed = command.Trim();
        foreach (var prefix in BuildCommandPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // bare "make" with or without args
        if (trimmed.Equals("make", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("make ", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static readonly Regex GoErrorLine =
        new(@"^\./[^:]+:\d+:\d+: (?!warning:)", RegexOptions.Compiled);

    private static List<string> ParseCompilerErrors(string output)
    {
        const int MaxErrors = 20;
        var errors = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            bool isDotNet  = trimmed.Contains("): error CS", StringComparison.OrdinalIgnoreCase)
                          || trimmed.Contains("): error FS", StringComparison.OrdinalIgnoreCase);
            bool isRust    = trimmed.StartsWith("error[", StringComparison.Ordinal);
            bool isGo      = GoErrorLine.IsMatch(trimmed);
            bool isGeneric = trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                          || trimmed.Contains(": error:", StringComparison.OrdinalIgnoreCase);

            if (isDotNet || isRust || isGo || isGeneric)
            {
                errors.Add(trimmed);
                if (errors.Count >= MaxErrors) break;
            }
        }
        return errors;
    }

    [Description("Write a script to a temp file and execute it.")]
    public async Task<string> RunScriptAsync(
        [Description("Script body.")] string script,
        [Description("Working directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 120)
    {
        var sudoDenial = CheckForSudo(script);
        if (sudoDenial is not null) return sudoDenial;

        var policyDenial = CheckShellPolicy(script);
        if (policyDenial is not null) return policyDenial;

        if (_approveCommand is not null && !await _approveCommand(script))
            return PluginResult.Denied("Shell script blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        var ext     = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var tmpFile = FuseraftPaths.NewTempFile("script", ext);

        try
        {
            await File.WriteAllTextAsync(tmpFile, script);

            if (!OperatingSystem.IsWindows())
            {
                // Make the script executable on Unix.
                var chmod = await ProcessHelper.RunAsync("chmod", $"+x \"{tmpFile}\"");
                if (!chmod.Succeeded)
                    return PluginResult.Error($"Failed to make script executable: {chmod.Stderr.Trim()}");
            }

            var result = await ProcessHelper.RunAsync(Shell, [ShellFlag, tmpFile], resolvedDir, timeoutSeconds);

            result = await WithWindowsPowerShellFallbackAsync(result, async () =>
            {
                // Re-materialize as .ps1 rather than reusing the .cmd file: PowerShell applies
                // script-file security policy (execution policy, etc.) based on extension.
                var psFile = FuseraftPaths.NewTempFile("script", ".ps1");
                try
                {
                    await File.WriteAllTextAsync(psFile, script);
                    return await ProcessHelper.RunAsync(
                        ProcessHelper.WindowsPowerShellPath.Value,
                        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", psFile],
                        resolvedDir, timeoutSeconds);
                }
                finally
                {
                    try { File.Delete(psFile); } catch { /* best effort */ }
                }
            });

            return result.ToPluginOutput();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    // Environment helpers

    [Description("Get an environment variable value.")]
    public string GetEnv([Description("Variable name.")] string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    [Description("Set an environment variable for this session.")]
    public string SetEnv(
        [Description("Variable name.")] string name,
        [Description("Value. Pass empty string to clear.")] string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return PluginResult.Error("Variable name must not be empty.");

        var target = EnvironmentVariableTarget.Process;
        if (string.IsNullOrEmpty(value))
        {
            Environment.SetEnvironmentVariable(name, null, target);
            return PluginResult.Ok($"Cleared environment variable '{name}'.");
        }

        Environment.SetEnvironmentVariable(name, value, target);
        return PluginResult.Ok($"Set {name}={value}");
    }

    [Description("Get the path of a program (like 'which').")]
    public async Task<string> WhichAsync([Description("Program name.")] string program)
    {
        var cmd = OperatingSystem.IsWindows() ? "where" : "which";
        var result = await ProcessHelper.RunAsync(cmd, program);
        return result.Succeeded
            ? result.Stdout.Trim()
            : PluginResult.NotFound($"'{program}' is not in PATH.");
    }

    [Description("Get the effective default working directory.")]
    public string GetWorkingDirectory() => _sandboxRoot ?? Directory.GetCurrentDirectory();

    [Description("Get a session-scoped temp directory (auto-deleted on exit).")]
    public string GetSessionTempDir()
    {
        if (_sessionTempDir is null)
        {
            lock (_tempDirLock)
            {
                if (_sessionTempDir is null)
                {
                    _sessionTempDir = FuseraftPaths.NewTempDir();
                }
            }
        }
        return _sessionTempDir;
    }

    // Background jobs

    [Description("Run a shell command in the background. Returns a job ID.")]
    public async Task<string> RunBackgroundAsync(
        [Description("Shell command.")] string command,
        [Description("Working directory.")] string? workingDirectory = null)
    {
        command = System.Net.WebUtility.HtmlDecode(command);

        var sudoDenial = CheckForSudo(command);
        if (sudoDenial is not null) return sudoDenial;

        var policyDenial = CheckShellPolicy(command);
        if (policyDenial is not null) return policyDenial;

        if (_approveCommand is not null && !await _approveCommand(command))
            return PluginResult.Denied("Shell command blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        var jobId      = Guid.NewGuid().ToString("N")[..8];
        var job        = new BackgroundJob(jobId);
        var workingDir = resolvedDir ?? Directory.GetCurrentDirectory();

        System.Diagnostics.Process process;
        try { process = StartProcess(Shell, [ShellFlag, command], workingDir); }
        catch (Exception ex)
        {
            return PluginResult.Error($"Failed to start background process: {ex.Message}");
        }
        WireOutputReaders(job, process);

        if (OperatingSystem.IsWindows())
            await RetryBackgroundJobViaPowerShellIfMismatchedAsync(job, process, command, workingDir);

        _jobs[jobId] = job;
        return PluginResult.Ok($"Background job started. Job ID: {jobId}\nCommand: {command}\nUse shell_job_status({jobId}) to check progress.");
    }

    [Description("Get the status of a background job.")]
    public string GetJobStatus(
        [Description("Job ID.")] string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return PluginResult.Error($"No background job with ID '{jobId}'. Use shell_job_status with an ID returned by shell_run_background.");

        if (job.IsRunning)
        {
            var recent = TailOutput(job.ReadOutput(), 500);
            return $"[RUNNING] Job {jobId}\n{(string.IsNullOrEmpty(recent) ? "(no output yet)" : $"Recent output:\n{recent}")}";
        }

        if (job.Killed)
            return $"[KILLED] Job {jobId} was terminated.";

        var exitCode = job.ExitCode ?? -1;
        var tail     = TailOutput(job.ReadOutput(), 1000);
        return exitCode == 0
            ? $"[COMPLETED] Job {jobId} exited 0 (success).\n{tail}"
            : $"[FAILED] Job {jobId} exited {exitCode}.\n{tail}";
    }

    [Description("Get the full output of a background job.")]
    public string GetJobOutput(
        [Description("Job ID.")] string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return PluginResult.Error($"No background job with ID '{jobId}'.");

        var output = job.ReadOutput();
        return string.IsNullOrEmpty(output)
            ? PluginResult.Info($"Job {jobId}: no output captured yet.")
            : output;
    }

    [Description("Kill a background job.")]
    public string KillJob(
        [Description("Job ID.")] string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return PluginResult.Error($"No background job with ID '{jobId}'.");

        if (!job.IsRunning)
            return PluginResult.Info($"Job {jobId} is not running (already completed or killed).");

        try
        {
            job.Process?.Kill(entireProcessTree: true);
            job.Killed = true;
            return PluginResult.Ok($"Job {jobId} killed.");
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Failed to kill job {jobId}: {ex.Message}");
        }
    }

    private static string TailOutput(string output, int maxChars)
    {
        if (output.Length <= maxChars) return output;
        return $"[… truncated …]\n{output[^maxChars..]}";
    }

    // Helpers

    // Checks the command against the configured ShellPolicy allow/deny lists.
    // Deny is evaluated first; a matching deny pattern blocks the command regardless of allow.
    // Allow is only evaluated when the allow list is non-empty; the command must contain at
    // least one allowed pattern to proceed.
    // Returns a [DENIED] string when blocked, null when safe.
    private string? CheckShellPolicy(string commandOrScript)
    {
        if (_shellPolicy is null) return null;

        if (_shellPolicy.Deny is { Count: > 0 })
        {
            foreach (var pattern in _shellPolicy.Deny)
            {
                if (commandOrScript.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return PluginResult.Denied(
                        $"Shell command blocked: matches configured deny pattern '{pattern}'.");
            }
        }

        if (_shellPolicy.Allow is { Count: > 0 })
        {
            bool allowed = _shellPolicy.Allow.Any(p =>
                commandOrScript.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
                return PluginResult.Denied(
                    $"Shell command blocked: not matched by any configured allow pattern. " +
                    $"Allowed: {string.Join(", ", _shellPolicy.Allow.Select(p => $"'{p}'"))}.");
        }

        return null;
    }

    // Detects sudo anywhere in a command string (including after ;, &&, ||, |, or newlines)
    // so agents cannot escalate privileges.  Returns a [DENIED] string when found, null when safe.
    private static readonly System.Text.RegularExpressions.Regex SudoPattern =
        new(@"(^|[;&|\n]\s*)sudo\b",
            System.Text.RegularExpressions.RegexOptions.Multiline |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? CheckForSudo(string commandOrScript)
    {
        if (SudoPattern.IsMatch(commandOrScript))
            return PluginResult.Denied(
                "sudo is not permitted. " +
                "Prefer non-privileged alternatives: pip install --user, python -m pip install --user, " +
                "pipx, or a virtual environment (python -m venv .venv && .venv/bin/pip install ...). " +
                "If elevated privileges are truly required, tell the user exactly which command to run " +
                "and they will run it themselves.");
        return null;
    }

    // Validates that the working directory stays within the sandbox.
    // When a sandbox is active and no directory is specified, defaults to the sandbox root
    // so commands never run in an uncontrolled directory.
    // Returns a [DENIED] error string on violation, null when safe.
    private string? ValidateWorkingDirectory(string? workingDirectory, out string? resolved)
    {
        if (_sandboxRoot is null)
        {
            resolved = workingDirectory;
            return null;
        }

        // Default to sandbox root when no directory is specified.
        resolved = Path.GetFullPath(workingDirectory ?? _sandboxRoot);

        var sandboxPrefix = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolvedCheck.StartsWith(sandboxPrefix, comparison))
            return PluginResult.Denied($"Working directory '{resolved}' is outside the configured sandbox '{_sandboxRoot}'.");

        return null;
    }

}
