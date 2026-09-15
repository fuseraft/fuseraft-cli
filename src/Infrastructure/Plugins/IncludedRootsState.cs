namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// The REPL's session-wide set of additional allowed roots (beyond the primary sandbox root),
/// shared by reference across every plugin that resolves paths through
/// <see cref="FileSystemSandbox"/> — <see cref="FileSystemPlugin"/>, <see cref="FileSystemManagementOps"/>,
/// <see cref="SearchPlugin"/>, <see cref="ShellPlugin"/>, <see cref="GitPlugin"/> — mirroring
/// how those classes already share per-turn <c>HashSet&lt;string&gt;</c> state by reference.
///
/// <para>
/// Two sources feed the same instance: <c>--include</c> seeds it once at REPL startup (see
/// <c>ReplCommand.cs</c>), and a HITL-approved sandbox-escape grant (a denied path the user
/// chose to allow on the spot) adds to it mid-session via <see cref="TryAdd"/> — from that
/// point on every plugin sharing this instance honors the grant immediately, with no further
/// plumbing. Session-scoped only: never persisted to disk, never survives past <c>/exit</c>.
/// </para>
/// </summary>
public sealed class IncludedRootsState
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<string> _roots;
    private readonly Lock _lock = new();
    private readonly bool _mutable;

    internal IncludedRootsState(IEnumerable<string>? seed = null)
    {
        _roots   = [.. seed ?? []];
        _mutable = true;
    }

    private IncludedRootsState(bool mutable)
    {
        _roots   = [];
        _mutable = mutable;
    }

    /// <summary>
    /// Empty, and — unlike a normal instance — permanently immutable: <see cref="TryAdd"/> is a
    /// guaranteed no-op. This is a single <c>static</c> instance shared as the fallback for
    /// every plugin constructed with no explicit <see cref="IncludedRootsState"/> (e.g. every
    /// orchestration <c>fuseraft run</c> plugin today, since only the REPL's <c>--include</c>
    /// wiring constructs a real one). Some of those plugins already carry a real HITL approval
    /// callback for unrelated actions (delete_file, etc.) — if this fallback were an ordinary
    /// mutable instance, an approved <see cref="TryGrantEscapeAsync"/> grant would silently
    /// mutate one shared, process-lifetime singleton, leaking a sandbox-escape grant from one
    /// run into every other plugin — a different agent in the same multi-agent run, or an
    /// entirely separate `fuseraft run` invocation — that also falls back to this instance.
    /// A grant against this instance still lets the *current* call through (see
    /// <see cref="TryGrantEscapeAsync"/>'s return value), it just isn't remembered — the safe
    /// failure mode is a redundant re-prompt next time, never a cross-run leak.
    /// </summary>
    internal static IncludedRootsState Empty { get; } = new(mutable: false);

    internal IReadOnlyList<string> Snapshot()
    {
        lock (_lock) return [.. _roots];
    }

    /// <summary>Adds <paramref name="root"/> if not already present (or contained in an existing
    /// root). Returns whether it was actually added. Always returns <see langword="false"/>
    /// on <see cref="Empty"/> — see its doc comment for why that's load-bearing, not incidental.</summary>
    internal bool TryAdd(string root)
    {
        if (!_mutable) return false;

        lock (_lock)
        {
            var normalized = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (_roots.Any(r => normalized.StartsWith(
                    r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison)))
                return false;
            _roots.Add(root);
            return true;
        }
    }

    /// <summary>
    /// Offers a HITL prompt to grant access to the directory containing a denied path, for the
    /// rest of this session. <paramref name="resolvedPath"/> is the already-resolved absolute
    /// path a <see cref="FileSystemSandbox.ResolveSafe"/>/<c>ResolveSafeDirectory</c> call just
    /// denied — its resolution doesn't depend on whether it was allowed, so callers don't need
    /// to re-resolve after a grant, only re-check containment (or simply proceed, since a grant
    /// always covers the exact path that triggered it).
    ///
    /// <para>
    /// Three-way result, not a plain bool, so a caller (see <see cref="DenyOrEscalateAsync"/>)
    /// can tell "no approval mechanism was even offered" apart from "a human was asked and said
    /// no" — this codebase already distinguishes those for every other HITL-declined action
    /// (e.g. <c>"Directory delete blocked by user."</c> in <c>FileSystemManagementOps</c>), so
    /// an escalation denial should read the same way, not fall back to the generic sandbox
    /// message as if no human had been involved at all.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="null"/> immediately, with no prompt, when
    /// <paramref name="approveEscape"/> is <see langword="null"/> (no approval mechanism wired —
    /// e.g. an orchestration agent with no HITL configured). Returns <see langword="false"/>
    /// when offered and declined. On approval (<see langword="true"/>), grants the *containing
    /// directory* of <paramref name="resolvedPath"/> (or the path itself if it's already a
    /// directory) — narrow enough to be a meaningful boundary decision, broad enough that a
    /// follow-up request for a sibling file doesn't re-prompt.
    /// </para>
    /// </summary>
    private async Task<bool?> TryGrantEscapeAsync(
        string resolvedPath, string toolName, Func<string, string, Task<bool>>? approveEscape)
    {
        if (approveEscape is null) return null;

        var grantDir = Directory.Exists(resolvedPath)
            ? resolvedPath
            : Path.GetDirectoryName(resolvedPath) ?? resolvedPath;

        if (!await approveEscape(toolName,
                $"{resolvedPath} is outside the current sandbox — grants '{grantDir}' for the rest of this session"))
            return false;

        TryAdd(grantDir);
        return true;
    }

    /// <summary>
    /// The single call every sandbox-check call site makes: given the denial a
    /// <c>ResolveSafe</c>/<c>ResolveSafeDirectory</c> call just returned (or <see langword="null"/>
    /// when it succeeded), offers an escalation prompt when there was a denial, and returns the
    /// error string the caller should propagate — or <see langword="null"/> to proceed. Distinct
    /// wording per outcome: the original sandbox message when no approval mechanism exists at
    /// all, a "blocked by user" message when a human explicitly declined, matching this
    /// codebase's convention for every other HITL-declined action.
    /// </summary>
    internal async Task<string?> DenyOrEscalateAsync(
        string? denial, string resolvedPath, string toolName, Func<string, string, Task<bool>>? approveEscape)
    {
        if (denial is null) return null;

        return await TryGrantEscapeAsync(resolvedPath, toolName, approveEscape) switch
        {
            true  => null,
            false => PluginResult.Denied("Sandbox-escape request blocked by user."),
            null  => denial,
        };
    }
}
