using System.Text.RegularExpressions;
using AgentGovernance.Hypervisor;
using AgentGovernance.Security;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// MAF function middleware that enforces the configured sandbox root
/// before any plugin function executes.
///
/// <para>
/// Call <see cref="WrapAgent"/> on each agent when
/// <c>Security.FileSystemSandboxPath</c> is set. Runs before the plugin function
/// body — if the check fails the function is never invoked and the agent receives
/// a <c>[DENIED]</c> error as its tool result.
/// </para>
///
/// <para>
/// Checks performed per function name:
/// <list type="bullet">
///   <item><b>read_file / write_file / delete_file / list_files</b> — resolves the <c>path</c> /
///       <c>directory</c> argument to its canonical absolute form and hard-denies it if it
///       falls outside the sandbox.</item>
///   <item><b>shell_run / shell_run_script</b> — hard-denies an out-of-sandbox
///       <c>workingDirectory</c>, then does a best-effort scan of the <c>command</c> /
///       <c>script</c> string for absolute paths that escape the sandbox. System binary
///       prefixes (<c>/usr/</c>, <c>/bin/</c>, etc.) are exempted so normal tool
///       invocations like <c>/usr/bin/dotnet</c> are not blocked.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Limitation:</b> shell command scanning is heuristic — shell escaping, variable
/// interpolation, and subshells can smuggle paths past regex matching. For hard
/// containment use the <c>CodeExecution</c> plugin (Docker) instead of <c>Shell</c>.
/// </para>
/// </summary>
public sealed class SandboxEnforcementFilter
{
    private readonly string _sandboxRoot;
    private readonly PromptInjectionDetector? _injectionDetector;
    private readonly ExecutionRing _ring;
    private readonly RingResourceLimits _limits;

    // Prefixes of OS directories that contain executables and shared libraries.
    private static readonly string[] SystemPrefixes = OperatingSystem.IsWindows()
        ? [@"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\"]
        : ["/usr/", "/bin/", "/sbin/", "/lib/", "/lib64/", "/opt/", "/nix/",
           "/run/current-system/", "/snap/"];

    // Matches tokens that look like absolute paths inside a shell command string.
    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![:\w])(/[^\s""'`;|&><(){}$\\]{2,}|[A-Za-z]:\\[^\s""'`;|&><(){}]+|\\\\[^\s""'`;|&><(){}]+)",
        RegexOptions.Compiled);

    private static readonly string[] FileSystemFunctions =
        ["read_file", "write_file", "delete_file", "list_files"];

    private static readonly string[] ShellFunctions =
        ["shell_run", "shell_run_script"];

    // Functions that modify state — blocked for Ring 3 agents.
    private static readonly string[] WriteFunctions =
        ["write_file", "delete_file", "shell_run", "shell_run_script"];

    // Functions that make outbound HTTP calls — blocked for Ring 3 agents.
    private static readonly string[] NetworkFunctions =
        ["http_request"];

    public SandboxEnforcementFilter(string sandboxRoot, PromptInjectionDetector? injectionDetector = null, ExecutionRing ring = ExecutionRing.Ring2)
    {
        _sandboxRoot       = Path.GetFullPath(ProcessHelper.ExpandHome(sandboxRoot));
        _injectionDetector = injectionDetector;
        _ring              = ring;
        _limits            = RingResourceLimits.Defaults[ring];
    }

    /// <summary>
    /// Wraps <paramref name="agent"/> with the sandbox enforcement middleware.
    /// Returns the middleware-wrapped agent.
    /// </summary>
    public AIAgent WrapAgent(AIAgent agent) =>
        agent.AsBuilder().Use(SandboxMiddleware).Build();

    // MAF function middleware
    private async ValueTask<object?> SandboxMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var denial = Inspect(context.Function.Name, context.Arguments);
        if (denial is not null)
            return denial;

        return await next(context, cancellationToken);
    }

    // Inspection

    private string? Inspect(string functionName, IReadOnlyDictionary<string, object?>? args)
    {
        // Ring check first — enforces trust-score-based privilege before path inspection.
        var ringDenial = InspectRing(functionName);
        if (ringDenial is not null) return ringDenial;

        if (FileSystemFunctions.Any(f =>
                string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase)))
            return InspectFileSystem(args);

        if (ShellFunctions.Any(f =>
                string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase)))
            return InspectShell(args);

        return null;
    }

    private string? InspectRing(string functionName)
    {
        if (!_limits.AllowWrites &&
            WriteFunctions.Any(f => string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase)))
            return PluginResult.Denied(
                $"Agent is in execution {_ring} and does not have write or shell privileges. " +
                $"Increase the agent's TrustScore (≥ 0.60) to allow write operations.");

        if (!_limits.AllowNetwork &&
            NetworkFunctions.Any(f => string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase)))
            return PluginResult.Denied(
                $"Agent is in execution {_ring} and does not have network privileges. " +
                $"Increase the agent's TrustScore (≥ 0.60) to allow network access.");

        return null;
    }

    private string? InspectFileSystem(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null) return null;
        foreach (var argName in (ReadOnlySpan<string>)["path", "directory"])
        {
            if (args.TryGetValue(argName, out var val) && val is string raw)
            {
                var denial = CheckPath(raw);
                if (denial is not null) return denial;
            }
        }
        return null;
    }

    private string? InspectShell(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null) return null;

        if (args.TryGetValue("workingDirectory", out var wd) && wd is string wdStr)
        {
            var denial = CheckPath(wdStr);
            if (denial is not null) return denial;
        }

        foreach (var argName in (ReadOnlySpan<string>)["command", "script"])
        {
            if (args.TryGetValue(argName, out var cmd) && cmd is string cmdStr)
            {
                var pathDenial = ScanCommandString(cmdStr);
                if (pathDenial is not null) return pathDenial;

                if (_injectionDetector is not null)
                {
                    var detection = _injectionDetector.Detect(cmdStr);
                    if (detection.IsInjection && detection.ThreatLevel >= ThreatLevel.High)
                        return PluginResult.Denied(
                            $"Shell command blocked: prompt injection detected " +
                            $"({detection.InjectionType}, confidence {detection.Confidence:P0}).");
                }
            }
        }

        return null;
    }

    // Helpers

    private string? CheckPath(string rawPath)
    {
        string resolved;
        try
        {
            var expandedPath = ProcessHelper.ExpandHome(rawPath);
            resolved = Path.IsPathRooted(expandedPath)
                ? Path.GetFullPath(expandedPath)
                : Path.GetFullPath(expandedPath, _sandboxRoot);
        }
        catch
        {
            return null;
        }

        return IsOutsideSandbox(resolved)
            ? PluginResult.Denied(
                $"Path '{resolved}' is outside the configured sandbox '{_sandboxRoot}'. " +
                $"All file operations must stay within the sandbox.")
            : null;
    }

    private string? ScanCommandString(string command)
    {
        foreach (Match m in AbsolutePathPattern.Matches(command))
        {
            var candidate = m.Value.Trim();

            if (IsSystemPath(candidate)) continue;

            if (candidate.All(c => c == '.' || c == '/' || c == '\\')) continue;

            string resolved;
            try { resolved = Path.GetFullPath(candidate); }
            catch (Exception) { continue; } // Path.GetFullPath throws on invalid/rooted path strings

            if (IsOutsideSandbox(resolved))
                return PluginResult.Denied(
                    $"Shell command references path '{resolved}' which is outside the " +
                    $"configured sandbox '{_sandboxRoot}'. Move the file into the sandbox " +
                    $"or remove the reference.");
        }

        return null;
    }

    private bool IsOutsideSandbox(string resolved)
    {
        var sandboxPrefix = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return !resolvedCheck.StartsWith(sandboxPrefix, comparison);
    }

    private static bool IsSystemPath(string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return SystemPrefixes.Any(prefix => path.StartsWith(prefix, comparison));
    }
}
