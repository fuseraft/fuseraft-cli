using System.ComponentModel;
using Microsoft.Extensions.AI;

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
public sealed class ShellPlugin : IDisposable
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

    private readonly string? _sandboxRoot;
    private readonly Func<string, Task<bool>>? _approveCommand;
    private readonly object _tempDirLock = new();
    private string? _sessionTempDir;

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
    }

    public ShellPlugin(string? sandboxRoot = null, Func<string, Task<bool>>? approveCommand = null)
    {
        _sandboxRoot    = sandboxRoot is not null ? Path.GetFullPath(ProcessHelper.ExpandHome(sandboxRoot)) : null;
        _approveCommand = approveCommand;
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

    [Description("Run a shell command and return stdout/stderr.")]
    public async Task<string> RunAsync(
        [Description("Shell command to execute.")] string command,
        [Description("Working directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 60)
    {
        // LLM outputs sometimes carry HTML entity encoding (e.g. &amp;&amp; instead of &&).
        // Decode before passing to the shell so commands execute as intended.
        command = System.Net.WebUtility.HtmlDecode(command);

        var sudoDenial = CheckForSudo(command);
        if (sudoDenial is not null) return sudoDenial;

        if (_approveCommand is not null && !await _approveCommand(command))
            return PluginResult.Denied("Shell command blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        var result = await ProcessHelper.RunAsync(
            Shell, [ShellFlag, command],
            resolvedDir, timeoutSeconds);

        return result.ToPluginOutput();
    }

    [Description("Write a script to a temp file and execute it.")]
    public async Task<string> RunScriptAsync(
        [Description("Script body.")] string script,
        [Description("Working directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 120)
    {
        var sudoDenial = CheckForSudo(script);
        if (sudoDenial is not null) return sudoDenial;

        if (_approveCommand is not null && !await _approveCommand(script))
            return PluginResult.Denied("Shell script blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        var ext     = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var tmpFile = Path.Combine(Path.GetTempPath(), $"fuseraft_{Guid.NewGuid():N}{ext}");

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
                    var path = Path.Combine(Path.GetTempPath(), $"fuseraft_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(path);
                    _sessionTempDir = path;
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

        if (_approveCommand is not null && !await _approveCommand(command))
            return PluginResult.Denied("Shell command blocked by user.");

        var denial = ValidateWorkingDirectory(workingDirectory, out var resolvedDir);
        if (denial is not null) return denial;

        var jobId   = Guid.NewGuid().ToString("N")[..8];
        var job     = new BackgroundJob(jobId);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = Shell,
            Arguments              = $"{ShellFlag} {command}",
            WorkingDirectory       = resolvedDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        job.Process = process;

        try { process.Start(); }
        catch (Exception ex)
        {
            return PluginResult.Error($"Failed to start background process: {ex.Message}");
        }

        process.StandardInput.Close();

        // Drain stdout and stderr concurrently into the job's output buffer.
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
