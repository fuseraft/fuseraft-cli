using System.Text.RegularExpressions;
using AgentGovernance.Hypervisor;
using AgentGovernance.Security;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using fuseraft.Core;
using fuseraft.Core.Models;

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
    private readonly Matcher? _changeEnvelopeMatcher;
    private readonly Matcher? _fsDenyMatcher;
    private readonly Matcher? _fsReadMatcher;
    private readonly Matcher? _fsWriteMatcher;

    // Prefixes of OS directories that contain executables and shared libraries.
    private static readonly string[] SystemPrefixes = OperatingSystem.IsWindows()
        ? [@"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\"]
        : ["/usr/", "/bin/", "/sbin/", "/lib/", "/lib64/", "/opt/", "/nix/",
           "/run/current-system/", "/snap/"];

    // fuseraft's own runtime state directory — always accessible regardless of project sandbox.
    // Agents must be able to read/write session artifacts (briefs, events, context summaries, etc.)
    // even when the project sandbox is locked down to the repo root.
    private static readonly string FuseraftHomePrefix =
        FuseraftPaths.ExpandPath("~/.fuseraft").TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    // Matches tokens that look like absolute paths inside a shell command string.
    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![:\w])(/[^\s""'`;|&><(){}$\\]{2,}|[A-Za-z]:\\[^\s""'`;|&><(){}]+|\\\\[^\s""'`;|&><(){}]+)",
        RegexOptions.Compiled);

    // Detects command substitution patterns that could smuggle arbitrary paths past the
    // regex scanner: $(...), `...`, and ${VAR} expansion. These constructs execute
    // subshells or dereference variables at runtime, making static path analysis
    // unreliable. Commands containing them are denied when a sandbox root is active
    // because the substituted value can reference any path on the filesystem.
    private static readonly Regex SubshellPattern = new(
        @"\$\([^)]*\)|`[^`]*`|\$\{[^}]*\}",
        RegexOptions.Compiled);

    private static readonly string[] FileSystemFunctions =
        ["read_file", "write_file", "delete_file", "list_files"];

    // Write-type extended functions that must always be routed through InspectFileSystem for
    // sandbox boundary checks, even when no FileSystemPermissions glob matchers are configured.
    // These functions create, modify, or remove paths and must stay within the sandbox root.
    private static readonly HashSet<string> SandboxedExtendedWriteFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch_file", "create_directory", "delete_directory", "set_permissions",
        "copy_file", "move_file",
    };

    private static readonly string[] ShellFunctions =
        ["shell_run", "shell_run_script"];

    // Functions that modify state — blocked for Ring 3 agents.
    private static readonly string[] WriteFunctions =
        ["write_file", "delete_file", "shell_run", "shell_run_script"];

    // Functions that make outbound HTTP calls — blocked for Ring 3 agents.
    private static readonly string[] NetworkFunctions =
        ["http_request"];

    // Write operations subject to the change envelope (distinct from the ring-level WriteFunctions
    // list which also covers shell — shell is too coarse-grained for path-level envelope checks).
    // copy_file/move_file are included because they create/overwrite files at their destination;
    // the InspectFileSystem loop applies the envelope to the destination arg only for mixed ops.
    private static readonly string[] EnvelopedFunctions =
        ["write_file", "patch_file", "delete_file", "copy_file", "move_file"];

    // Functions whose path content is protected by Read globs (actual file content is returned).
    private static readonly HashSet<string> ContentReadFsFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "grep_file", "get_file_summary",
    };

    // Functions that access only metadata (names, sizes, timestamps) — exempt from Read globs
    // but still subject to sandbox boundary and Deny glob checks.
    private static readonly HashSet<string> MetadataFsFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "list_files", "list_directory", "get_file_info",
    };

    // Functions that write to user-specified paths.
    private static readonly HashSet<string> WriteOnlyFsFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "patch_file", "delete_file", "create_directory",
        "delete_directory", "set_permissions",
    };

    // Functions where source is read and destination is written — each arg type gets its own glob check.
    private static readonly HashSet<string> MixedReadWriteFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "copy_file", "move_file",
    };

    // Functions that write internal metadata about a path — Deny glob applies to the path arg
    // but write/read globs and the change envelope do not (the write target is .fuseraft/summaries/).
    private static readonly HashSet<string> DenyCheckedFsFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "save_file_summary",
    };

    // All extended FS functions eligible for glob-level checks (used for routing in Inspect).
    // Computed from the five specific sets so additions to those sets are automatically reflected here.
    private static readonly HashSet<string> AllExtendedFsFunctions = new(
        ContentReadFsFunctions
            .Concat(MetadataFsFunctions)
            .Concat(WriteOnlyFsFunctions)
            .Concat(MixedReadWriteFunctions)
            .Concat(DenyCheckedFsFunctions),
        StringComparer.OrdinalIgnoreCase);

    // Arg names that may carry file/directory paths across all filesystem functions.
    private static readonly string[] FsPathArgNames = ["path", "directory", "source", "destination"];

    public SandboxEnforcementFilter(
        string sandboxRoot,
        PromptInjectionDetector? injectionDetector = null,
        ExecutionRing ring = ExecutionRing.Ring2,
        IReadOnlyList<string>? changeEnvelope = null,
        FileSystemPermissions? fsPermissions = null)
    {
        _sandboxRoot       = FuseraftPaths.ExpandPath(sandboxRoot);
        _injectionDetector = injectionDetector;
        _ring              = ring;
        _limits            = RingResourceLimits.Defaults[ring];

        if (changeEnvelope is { Count: > 0 })
        {
            _changeEnvelopeMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var pattern in changeEnvelope)
                _changeEnvelopeMatcher.AddInclude(pattern);
        }

        if (fsPermissions?.Deny is { Count: > 0 } deny)
        {
            _fsDenyMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in deny) _fsDenyMatcher.AddInclude(p);
        }

        if (fsPermissions?.Read is { Count: > 0 } read)
        {
            _fsReadMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in read) _fsReadMatcher.AddInclude(p);
        }

        if (fsPermissions?.Write is { Count: > 0 } write)
        {
            _fsWriteMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in write) _fsWriteMatcher.AddInclude(p);
        }
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

        // Core FS functions are always sandboxed; write-type extended functions are also
        // always sandboxed (boundary check only). Other extended functions are routed when
        // any glob matcher is configured so they get sandbox + deny/read/write checks.
        bool hasGlobMatcher = _fsDenyMatcher is not null || _fsReadMatcher is not null || _fsWriteMatcher is not null;
        bool isFsFunction = FileSystemFunctions.Any(f =>
                string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase))
            || SandboxedExtendedWriteFunctions.Contains(functionName)
            || (hasGlobMatcher && AllExtendedFsFunctions.Contains(functionName));

        if (isFsFunction)
            return InspectFileSystem(functionName, args);

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

    private string? InspectFileSystem(string functionName, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null) return null;

        bool isMixedOp   = MixedReadWriteFunctions.Contains(functionName);
        bool isMetadata  = MetadataFsFunctions.Contains(functionName);
        bool isDenyOnly  = DenyCheckedFsFunctions.Contains(functionName);
        bool isEnveloped = _changeEnvelopeMatcher is not null &&
            EnvelopedFunctions.Any(f => string.Equals(f, functionName, StringComparison.OrdinalIgnoreCase));
        bool isContentRead = _fsReadMatcher  is not null && ContentReadFsFunctions.Contains(functionName);
        bool isWriteOp     = _fsWriteMatcher is not null && WriteOnlyFsFunctions.Contains(functionName);

        foreach (var argName in (ReadOnlySpan<string>)FsPathArgNames)
        {
            if (!args.TryGetValue(argName, out var val) || val is not string raw) continue;

            // 1. Sandbox check — deny if outside configured root.
            var sandboxDenial = CheckPath(raw);
            if (sandboxDenial is not null) return sandboxDenial;

            // 2. Deny glob — hard-blocks matching paths for all FS functions.
            if (_fsDenyMatcher is not null)
            {
                var denyDenial = CheckGlob(raw, _fsDenyMatcher, matchMeansDeny: true,
                    "Path is blocked by a configured FileSystem deny rule.");
                if (denyDenial is not null) return denyDenial;
            }

            // Metadata and deny-only functions stop here — no read/write glob or envelope checks.
            if (isMetadata || isDenyOnly) continue;

            bool isSourceArg = string.Equals(argName, "source",      StringComparison.OrdinalIgnoreCase);
            bool isDestArg   = string.Equals(argName, "destination", StringComparison.OrdinalIgnoreCase);

            // 3. Change envelope (existing brownfield feature).
            //    Mixed ops: envelope applies only to the destination (the write target).
            if (isEnveloped && (!isMixedOp || isDestArg))
            {
                var envelopeDenial = CheckEnvelope(raw);
                if (envelopeDenial is not null) return envelopeDenial;
            }

            // 4. Write glob.
            //    Pure write ops: all path args.
            //    Mixed ops (copy_file/move_file): destination only — the source is read, not written.
            bool applyWriteGlob = isWriteOp || (_fsWriteMatcher is not null && isMixedOp && isDestArg);
            if (applyWriteGlob)
            {
                var writeDenial = CheckGlob(raw, _fsWriteMatcher!, matchMeansDeny: false,
                    "Path is outside the configured FileSystem write permissions.");
                if (writeDenial is not null) return writeDenial;
            }

            // 5. Read glob.
            //    Content-read ops: all path args.
            //    Mixed ops (copy_file/move_file): source only — the destination is written, not read.
            bool applyReadGlob = isContentRead || (_fsReadMatcher is not null && isMixedOp && isSourceArg);
            if (applyReadGlob)
            {
                var readDenial = CheckGlob(raw, _fsReadMatcher!, matchMeansDeny: false,
                    "Path is outside the configured FileSystem read permissions.");
                if (readDenial is not null) return readDenial;
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
                // Deny subshell constructs ($(...), backticks, ${VAR}) — the substituted
                // value is unknown at static analysis time and can reference any path.
                var subshellMatch = SubshellPattern.Match(cmdStr);
                if (subshellMatch.Success)
                    return PluginResult.Denied(
                        $"Shell command contains a command substitution or variable expansion " +
                        $"('{subshellMatch.Value}') that cannot be statically verified against " +
                        $"the sandbox. Rewrite the command without subshells, or use the " +
                        $"CodeExecution plugin (Docker) for commands that require substitution.");

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

    // Evaluates a glob matcher against a resolved relative path.
    // When matchMeansDeny=true (deny list): returns a denial when the path matches.
    // When matchMeansDeny=false (allow list): returns a denial when the path does NOT match.
    private string? CheckGlob(string rawPath, Matcher matcher, bool matchMeansDeny, string reason)
    {
        string resolved;
        try
        {
            var expanded = ProcessHelper.ExpandHome(rawPath);
            resolved = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(expanded, _sandboxRoot);
        }
        catch { return null; }

        var relative = Path.GetRelativePath(_sandboxRoot, resolved).Replace('\\', '/');
        bool matches = matcher.Match(relative).HasMatches;

        return (matchMeansDeny ? matches : !matches)
            ? PluginResult.Denied($"[DENIED] '{relative}': {reason}")
            : null;
    }

    private string? CheckEnvelope(string rawPath)
    {
        string resolved;
        try
        {
            var expanded = ProcessHelper.ExpandHome(rawPath);
            resolved = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(expanded, _sandboxRoot);
        }
        catch { return null; }

        var relative = Path.GetRelativePath(_sandboxRoot, resolved).Replace('\\', '/');
        if (!_changeEnvelopeMatcher!.Match(relative).HasMatches)
            return PluginResult.Denied(
                $"Path '{relative}' is outside the configured change envelope. " +
                $"Only files matching the declared envelope globs may be written in this session. " +
                $"Ask the Planner to expand the scope if this file needs to change.");

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

        return !resolvedCheck.StartsWith(sandboxPrefix, comparison)
            && !resolvedCheck.StartsWith(FuseraftHomePrefix, comparison);
    }

    private static bool IsSystemPath(string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return SystemPrefixes.Any(prefix => path.StartsWith(prefix, comparison));
    }
}
