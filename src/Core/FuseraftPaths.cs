namespace fuseraft.Core;

/// <summary>
/// Central registry of all .fuseraft directory paths used by fuseraft-cli.
/// </summary>
public static class FuseraftPaths
{
    // ── Global (~/.fuseraft/) ─────────────────────────────────────────────────
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GlobalRoot       => Path.Combine(Home, ".fuseraft");
    public static string GlobalConfig     => Path.Combine(GlobalRoot, "config");
    public static string GlobalKeyFile    => Path.Combine(GlobalRoot, ".key");
    public static string GlobalSessions   => Path.Combine(GlobalRoot, "sessions");
    public static string GlobalCrashDumps => Path.Combine(GlobalRoot, "crashdump");
    public static string GlobalScratchpad => Path.Combine(GlobalRoot, "scratchpad");
    public static string GlobalMemoryRepl => Path.Combine(GlobalRoot, "memory", "repl");
    public static string GlobalMemoryAgent(string name) => Path.Combine(GlobalRoot, "memory", "agents", name);

    // ── Local (.fuseraft/ relative to CWD) ───────────────────────────────────
    public const string LocalRoot = ".fuseraft";

    // logs/ — append-only diagnostic and observability files
    public const string LocalLogs           = ".fuseraft/logs";
    public const string LocalEventsLog      = ".fuseraft/logs/events.jsonl";
    public const string LocalReplEventsLog  = ".fuseraft/logs/repl_events.jsonl";
    public const string LocalProviderErrors = ".fuseraft/logs/provider_errors.jsonl";
    public const string LocalAppLog         = ".fuseraft/logs/app.log";

    // state/ — session-scoped runtime state files
    public const string LocalState        = ".fuseraft/state";
    public const string LocalChanges      = ".fuseraft/state/changes.json";
    public const string LocalIntents      = ".fuseraft/state/intents.json";
    public const string LocalEvidence     = ".fuseraft/state/evidence.json";
    public const string LocalFileVersions = ".fuseraft/state/file_versions.json";

    // Agent artifacts and validator inputs (user-visible at root)
    public const string LocalBrief           = ".fuseraft/brief.json";
    public const string LocalTestReport      = ".fuseraft/test-report.json";
    public const string LocalChatroom        = ".fuseraft/chatroom.jsonl";
    public const string LocalConventions     = ".fuseraft/conventions.json";
    public const string LocalBrownfieldBrief = ".fuseraft/brief.brownfield.json";
    public const string LocalMemoryRefs      = ".fuseraft/memory_refs.json";

    // Already-subdirectorized paths (unchanged locations)
    public const string LocalContext   = ".fuseraft/context";
    public const string LocalSummaries = ".fuseraft/summaries";
}
