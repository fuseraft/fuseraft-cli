namespace fuseraft.Core;

/// <summary>
/// Central registry of all .fuseraft directory paths used by fuseraft-cli.
/// </summary>
public static class FuseraftPaths
{
    // Global (~/.fuseraft/)
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GlobalRoot       => Path.Combine(Home, ".fuseraft");
    public static string GlobalConfig     => Path.Combine(GlobalRoot, "config");
    public static string GlobalKeyFile    => Path.Combine(GlobalRoot, ".key");
    public static string GlobalSessions     => Path.Combine(GlobalRoot, "sessions");
    public static string GlobalReplSessions => Path.Combine(GlobalRoot, "repl-sessions");
    public static string GlobalCrashDumps => Path.Combine(GlobalRoot, "crashdump");
    public static string GlobalScratchpad => Path.Combine(GlobalRoot, "scratchpad");
    public static string GlobalSkills      => Path.Combine(GlobalRoot, "skills");

    // Path utilities

    /// <summary>
    /// Expands a leading <c>~</c> to the user home directory and returns an absolute,
    /// normalized path. Equivalent to <c>Path.GetFullPath(ExpandHome(path))</c>.
    /// </summary>
    public static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(path.Length > 2 ? Path.Combine(home, path[2..]) : home);
        }
        return Path.GetFullPath(path);
    }
    public static string GlobalSkillsIndex      => Path.Combine(GlobalRoot, "skills", "index.db");
    public static string GlobalSkillCurationLog => Path.Combine(GlobalRoot, "skill-curation.jsonl");
    public static string GlobalSchedule    => Path.Combine(GlobalRoot, "schedule");
    public static string GlobalMemoryRepl => Path.Combine(GlobalRoot, "memory", "repl");
    public static string GlobalMemoryAgent(string name) => Path.Combine(GlobalRoot, "memory", "agents", name);

    // Local (.fuseraft/ relative to CWD)
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

    // artifacts/ — structured agent-written documents read by validators
    public const string LocalArtifacts       = ".fuseraft/artifacts";
    public const string LocalBrief           = ".fuseraft/artifacts/brief.json";
    public const string LocalTestReport      = ".fuseraft/artifacts/test-report.json";
    public const string LocalConventions     = ".fuseraft/artifacts/conventions.json";
    public const string LocalBrownfieldBrief = ".fuseraft/artifacts/brief.brownfield.json";

    // comms/ — cross-agent communication channels
    public const string LocalComms    = ".fuseraft/comms";
    public const string LocalChatroom = ".fuseraft/comms/chatroom.jsonl";

    // memory/ (local) — session-scoped memory reference index
    public const string LocalMemory     = ".fuseraft/memory";
    public const string LocalMemoryRefs = ".fuseraft/memory/memory_refs.json";

    // docs/ — agent-written markdown documents (research, reports, drafts, notes)
    public const string LocalDocs = ".fuseraft/docs";

    // tests/ — tester-created test scripts and fixture files (any language/format)
    public const string LocalTests        = ".fuseraft/tests";
    public const string LocalTestFixtures = ".fuseraft/tests/fixtures";

    // Already-subdirectorized paths (unchanged locations)
    public const string LocalContext   = ".fuseraft/context";
    public const string LocalSummaries = ".fuseraft/summaries";

    /// <summary>
    /// Returns a compact orientation block that tells agents exactly what is in the
    /// local <c>.fuseraft/</c> directory so they never need to scan it with
    /// <c>list_files</c> or <c>read_file</c> to discover its layout.
    /// Inject this into every agent system prompt at session start.
    /// </summary>
    /// <param name="includeLogs">
    /// When <c>false</c>, omits the <c>logs/</c> entries. Pass <c>false</c> in REPL mode
    /// where the session block in the system prompt already lists the log paths and
    /// directs the agent to use the <c>repl_session_*</c> tools for log access.
    /// </param>
    public static string BuildFolderOrientationBlock(bool includeLogs = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## .fuseraft/ — fuseraft-cli runtime metadata (do not scan)");
        sb.AppendLine("This directory is managed by fuseraft-cli. Never call list_files or explore .fuseraft/ — reference these paths directly when needed:");
        if (includeLogs)
        {
            sb.AppendLine("  .fuseraft/logs/events.jsonl               — agent/orchestration event log (JSONL)");
            sb.AppendLine("  .fuseraft/logs/repl_events.jsonl          — REPL event log (JSONL)");
            sb.AppendLine("  .fuseraft/logs/app.log                    — application log");
        }
        sb.AppendLine("  .fuseraft/state/changes.json              — tool-call change log");
        sb.AppendLine("  .fuseraft/state/intents.json              — in-progress intent records (consult before repeating work)");
        sb.AppendLine("  .fuseraft/state/evidence.json             — structured evidence graph");
        sb.AppendLine("  .fuseraft/state/file_versions.json        — per-file versioned write counters");
        sb.AppendLine("  .fuseraft/artifacts/brief.json            — task brief (if present)");
        sb.AppendLine("  .fuseraft/artifacts/brief.brownfield.json — brownfield discovery brief (if present)");
        sb.AppendLine("  .fuseraft/artifacts/test-report.json      — tester output / validator input (if present)");
        sb.AppendLine("  .fuseraft/artifacts/conventions.json      — brownfield convention profile (if present)");
        sb.AppendLine("  .fuseraft/comms/chatroom.jsonl            — cross-agent chatroom messages (if present)");
        sb.AppendLine("  .fuseraft/docs/                           — write all markdown notes, reports, and drafts here");
        sb.AppendLine("  .fuseraft/tests/                          — write all test scripts and test support files here");
        sb.AppendLine("  .fuseraft/tests/fixtures/                 — seed data, stubs, and fixture files");
        sb.AppendLine("  .fuseraft/context/                        — injected reference documents (see .fuseraft/context/index.json)");
        sb.Append(    "  .fuseraft/summaries/                      — compaction summaries");
        return sb.ToString();
    }
}
