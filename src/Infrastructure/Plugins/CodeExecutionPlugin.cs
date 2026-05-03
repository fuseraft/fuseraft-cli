using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents Docker-backed code execution with two modes:
///
/// <list type="bullet">
///   <item><b>Sandboxed one-shot</b> — each call runs in a fresh, isolated container
///   (<c>--network none</c>, memory + CPU capped, auto-removed).</item>
///   <item><b>REPL sessions</b> (Python and Node only) — accumulated code is replayed
///   with stdout suppressed each turn so variables and functions stay in scope, but
///   only the current turn's output is returned to the agent.</item>
/// </list>
///
/// Call <c>check_docker</c> first to confirm Docker is available.
/// </summary>
public sealed class CodeExecutionPlugin
{
    // Language catalogue

    private sealed record DockerLanguage(
        string Image,
        string Extension,
        string Command,
        bool SupportsRepl);

    private static readonly IReadOnlyDictionary<string, DockerLanguage> Languages =
        new Dictionary<string, DockerLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            ["python"]     = new("python:3.12-slim",    ".py", "python3 /code/script.py",                        true),
            ["py"]         = new("python:3.12-slim",    ".py", "python3 /code/script.py",                        true),
            ["node"]       = new("node:20-slim",        ".js", "node /code/script.js",                           true),
            ["javascript"] = new("node:20-slim",        ".js", "node /code/script.js",                           true),
            ["js"]         = new("node:20-slim",        ".js", "node /code/script.js",                           true),
            ["bash"]       = new("bash:5.2",            ".sh", "bash /code/script.sh",                           false),
            ["sh"]         = new("bash:5.2",            ".sh", "bash /code/script.sh",                           false),
            ["go"]         = new("golang:1.23-alpine",  ".go", "go run /code/script.go",                         false),
            ["rust"]       = new("rust:1-slim",         ".rs", "sh -c 'rustc /code/script.rs -o /tmp/p && /tmp/p'", false),
        };

    // Docker resource limits applied to every container.
    private const string DockerLimits = "--network none --memory 256m --cpus 0.5";

    // Sentinel used to separate accumulated (suppressed) output from new output.
    private const string Separator = "___FUSERAFT_REPL_SEP___";

    // Session file written to the working directory so all agents share it.
    private const string SessionFile = ".fuseraft-repl-sessions.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim _sessionLock = new(1, 1);

    // check_docker

    [Description("Verify Docker is installed and running.")]
    public async Task<string> CheckDockerAsync()
    {
        // Check the CLI is on PATH.
        var which = await ProcessHelper.RunAsync(
            OperatingSystem.IsWindows() ? "where" : "which", "docker");

        if (!which.Succeeded)
            return PluginResult.Fail("Docker CLI not found in PATH. Install Docker from https://docs.docker.com/get-docker/");

        // Check the daemon is reachable.
        var info = await ProcessHelper.RunAsync("docker", "info --format '{{.ServerVersion}}'", timeoutSeconds: 10);

        return info.Succeeded
            ? PluginResult.Ok($"Docker is available. Server version: {info.Stdout.Trim()}")
            : PluginResult.Fail($"Docker CLI found but daemon is not running.\n{info.Stderr.Trim()}");
    }

    // sandbox_run

    [Description("Run a code snippet in an isolated Docker container. No state retained between calls.")]
    public async Task<string> SandboxRunAsync(
        [Description("Language: python, node, bash, go, rust.")] string language,
        [Description("Code to execute.")] string code,
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        if (!Languages.TryGetValue(language, out var lang))
            return UnsupportedLanguage(language);

        return await RunInContainerAsync(lang, code, timeoutSeconds);
    }

    // repl_start

    [Description("Start a REPL session (python or node). Returns a session ID.")]
    public async Task<string> ReplStartAsync(
        [Description("Language: python or node.")] string language)
    {
        if (!Languages.TryGetValue(language, out var lang) || !lang.SupportsRepl)
            return PluginResult.Error($"REPL sessions are only supported for python and node. Use sandbox_run for '{language}'.");

        await _sessionLock.WaitAsync();
        try
        {
            var sessions = await LoadSessionsAsync();
            var id = Guid.NewGuid().ToString("N")[..8];

            sessions[id] = new ReplSession
            {
                Language        = language.ToLowerInvariant(),
                AccumulatedCode = string.Empty,
                CreatedAt       = DateTime.UtcNow,
                LastUsedAt      = DateTime.UtcNow
            };

            await SaveSessionsAsync(sessions);
            return PluginResult.Ok($"REPL session started.\nSession ID : {id}\nLanguage   : {language}\nUse repl_exec to run code in this session.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // repl_exec

    [Description("Execute code in a REPL session. Prior state (variables, imports) is retained.")]
    public async Task<string> ReplExecAsync(
        [Description("Session ID.")] string sessionId,
        [Description("Code to execute.")] string code,
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        Dictionary<string, ReplSession> sessions;
        ReplSession? session;

        await _sessionLock.WaitAsync();
        try
        {
            sessions = await LoadSessionsAsync();

            if (!sessions.TryGetValue(sessionId, out session))
                return PluginResult.Error($"Session '{sessionId}' not found. Use repl_start to create one.");

            if (!Languages.TryGetValue(session.Language, out _))
                return PluginResult.Error($"Unknown language '{session.Language}' in session.");
        }
        finally
        {
            _sessionLock.Release();
        }

        if (!Languages.TryGetValue(session.Language, out var lang))
            return PluginResult.Error($"Unknown language '{session.Language}' in session.");

        var script = BuildReplScript(session.Language, session.AccumulatedCode, code);
        var result = await RunInContainerAsync(lang, script, timeoutSeconds);

        // Extract only the output produced after the separator.
        var output = ExtractNewOutput(result);

        await _sessionLock.WaitAsync();
        try
        {
            // Re-read to avoid overwriting concurrent changes, then update this session.
            sessions = await LoadSessionsAsync();
            if (sessions.TryGetValue(sessionId, out var latest))
            {
                latest.AccumulatedCode = string.IsNullOrWhiteSpace(latest.AccumulatedCode)
                    ? code
                    : latest.AccumulatedCode + "\n" + code;
                latest.LastUsedAt = DateTime.UtcNow;
            }
            await SaveSessionsAsync(sessions);
        }
        finally
        {
            _sessionLock.Release();
        }

        return output;
    }

    // repl_reset

    [Description("Clear accumulated code in a REPL session without ending it.")]
    public async Task<string> ReplResetAsync(
        [Description("Session ID to reset.")] string sessionId)
    {
        await _sessionLock.WaitAsync();
        try
        {
            var sessions = await LoadSessionsAsync();

            if (!sessions.TryGetValue(sessionId, out var session))
                return PluginResult.Error($"Session '{sessionId}' not found.");

            session.AccumulatedCode = string.Empty;
            session.LastUsedAt = DateTime.UtcNow;
            await SaveSessionsAsync(sessions);
        }
        finally
        {
            _sessionLock.Release();
        }

        return PluginResult.Ok($"Session '{sessionId}' reset. Accumulated code cleared.");
    }

    // repl_stop

    [Description("Remove a REPL session.")]
    public async Task<string> ReplStopAsync(
        [Description("Session ID to stop.")] string sessionId)
    {
        await _sessionLock.WaitAsync();
        try
        {
            var sessions = await LoadSessionsAsync();

            if (!sessions.Remove(sessionId))
                return PluginResult.Error($"Session '{sessionId}' not found.");

            await SaveSessionsAsync(sessions);
        }
        finally
        {
            _sessionLock.Release();
        }

        return PluginResult.Ok($"Session '{sessionId}' stopped and removed.");
    }

    // Execution helpers

    private static async Task<string> RunInContainerAsync(DockerLanguage lang, string code, int timeoutSeconds)
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(), $"fuseraft_exec_{Guid.NewGuid():N}{lang.Extension}");

        try
        {
            await File.WriteAllTextAsync(tempFile, code);

            // Normalise path separators for the Docker -v flag.
            var hostPath = tempFile.Replace('\\', '/');
            var containerPath = $"/code/script{lang.Extension}";

            var args = $"run --rm {DockerLimits} -v \"{hostPath}:{containerPath}:ro\" {lang.Image} {lang.Command}";
            var result = await ProcessHelper.RunAsync("docker", args, timeoutSeconds: timeoutSeconds);

            return FormatResult(result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    private static string FormatResult(ProcessResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"EXIT CODE: {result.ExitCode}");

        var stdout = result.Stdout.TrimEnd();
        var stderr = result.Stderr.TrimEnd();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("OUTPUT:");
            sb.AppendLine(stdout);
        }
        else
        {
            sb.AppendLine("OUTPUT: (none)");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("STDERR:");
            sb.AppendLine(stderr);
        }

        return sb.ToString().TrimEnd();
    }

    // REPL script builders

    private static string BuildReplScript(string language, string accumulated, string newCode)
    {
        return language switch
        {
            "python" or "py" => BuildPythonReplScript(accumulated, newCode),
            "node" or "js" or "javascript" => BuildNodeReplScript(accumulated, newCode),
            _ => newCode
        };
    }

    private static string BuildPythonReplScript(string accumulated, string newCode)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(accumulated))
        {
            // Redirect stdout while replaying accumulated code so prior output is suppressed.
            sb.AppendLine("import sys as ___fs_sys, io as ___fs_io");
            sb.AppendLine("___fs_sys.stdout = ___fs_io.StringIO()");
            sb.AppendLine();
            sb.AppendLine("# --- accumulated ---");
            sb.AppendLine(accumulated);
            sb.AppendLine("# --- end accumulated ---");
            sb.AppendLine();
            sb.AppendLine("___fs_sys.stdout = ___fs_sys.__stdout__");
        }

        // Emit separator so we can slice the new output.
        sb.AppendLine($"print(\"{Separator}\", flush=True)");
        sb.AppendLine();
        sb.AppendLine("# --- new code ---");
        sb.AppendLine(newCode);

        return sb.ToString();
    }

    private static string BuildNodeReplScript(string accumulated, string newCode)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(accumulated))
        {
            // Suppress stdout while replaying accumulated code.
            sb.AppendLine("const ___fs_write = process.stdout.write.bind(process.stdout);");
            sb.AppendLine("process.stdout.write = () => true;");
            sb.AppendLine();
            sb.AppendLine("// --- accumulated ---");
            sb.AppendLine(accumulated);
            sb.AppendLine("// --- end accumulated ---");
            sb.AppendLine();
            sb.AppendLine("process.stdout.write = ___fs_write;");
        }

        sb.AppendLine($"process.stdout.write(\"{Separator}\\n\");");
        sb.AppendLine();
        sb.AppendLine("// --- new code ---");
        sb.AppendLine(newCode);

        return sb.ToString();
    }

    private static string ExtractNewOutput(string rawOutput)
    {
        var idx = rawOutput.IndexOf(Separator, StringComparison.Ordinal);
        if (idx < 0) return rawOutput;  // separator not found — return everything

        var after = rawOutput[(idx + Separator.Length)..].TrimStart('\r', '\n');
        return string.IsNullOrWhiteSpace(after) ? "(no output)" : after.TrimEnd();
    }

    // Session persistence

    private static async Task<Dictionary<string, ReplSession>> LoadSessionsAsync()
    {
        if (!File.Exists(SessionFile))
            return new Dictionary<string, ReplSession>();

        try
        {
            var json = await File.ReadAllTextAsync(SessionFile);
            return JsonSerializer.Deserialize<Dictionary<string, ReplSession>>(json, JsonOptions)
                   ?? new Dictionary<string, ReplSession>();
        }
        catch
        {
            return new Dictionary<string, ReplSession>();
        }
    }

    private static async Task SaveSessionsAsync(Dictionary<string, ReplSession> sessions)
    {
        var json = JsonSerializer.Serialize(sessions, JsonOptions);
        await File.WriteAllTextAsync(SessionFile, json);
    }

    // Helpers

    private static string UnsupportedLanguage(string language)
    {
        var supported = string.Join(", ", Languages.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order());
        return PluginResult.Error($"Unsupported language '{language}'. Supported: {supported}");
    }

    // Models

    private sealed class ReplSession
    {
        public string Language { get; set; } = string.Empty;
        public string AccumulatedCode { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsedAt { get; set; }
    }
}
