using System.Runtime.InteropServices;

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

    // Centralized temp directory — all fuseraft-generated temp files land here.
    public static string SystemTempRoot => Path.Combine(Path.GetTempPath(), "fuseraft");

    public static string NewTempFile(string prefix, string ext)
    {
        Directory.CreateDirectory(SystemTempRoot);
        return Path.Combine(SystemTempRoot, $"{prefix}_{Guid.NewGuid():N}{ext}");
    }

    public static string NewTempDir()
    {
        var path = Path.Combine(SystemTempRoot, $"session_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

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

    // logs/ — append-only diagnostic and observability files
    public const string LocalLogs           = ".fuseraft/logs";
    public const string LocalEventsLog      = ".fuseraft/logs/events.jsonl";
    public const string LocalReplEventsLog  = ".fuseraft/logs/repl_events.jsonl";
    public const string LocalProviderErrors = ".fuseraft/logs/provider_errors.jsonl";
    public const string LocalAppLog         = ".fuseraft/logs/app.log";

    // state/ — session-scoped runtime state files
    public const string LocalState          = ".fuseraft/state";
    public const string LocalChanges        = ".fuseraft/state/changes.json";
    public const string LocalIntents        = ".fuseraft/state/sessions/{session_id}/intents.json";
    public const string LocalSessionContext = ".fuseraft/state/sessions/{session_id}/context_summary.md";
    public const string LocalEvidence       = ".fuseraft/state/evidence.json";
    public const string LocalProvenance     = ".fuseraft/state/provenance.json";
    public const string LocalFileVersions       = ".fuseraft/state/file_versions.json";
    public const string LocalKnowledgeFindings  = ".fuseraft/state/knowledge_findings.json";

    // artifacts/ — structured agent-written documents read by validators
    // Brief paths include {session_id}, expanded at runtime via ExpandSessionId.
    public const string LocalSessionReadCache    = ".fuseraft/artifacts/sessions/{session_id}/read_cache.json";
    public const string LocalSessionToolArtifacts = ".fuseraft/artifacts/sessions/{session_id}/tool-results";
    public const string LocalBrief            = ".fuseraft/artifacts/sessions/{session_id}/brief.json";
    public const string LocalTestReport      = ".fuseraft/artifacts/test-report.json";
    public const string LocalConventions     = ".fuseraft/artifacts/sessions/{session_id}/conventions.json";
    public const string LocalBrownfieldBrief = ".fuseraft/artifacts/sessions/{session_id}/brief.brownfield.json";

    /// <summary>Expands the <c>{session_id}</c> token in a path with the given session ID.</summary>
    public static string ExpandSessionId(string path, string sessionId) =>
        path.Replace("{session_id}", sessionId, StringComparison.Ordinal);

    // comms/ — cross-agent communication channels
    public const string LocalChatroom = ".fuseraft/comms/sessions/{session_id}/chatroom.jsonl";

    // memory/ (local) — session-scoped memory reference index
    public const string LocalMemoryRefs = ".fuseraft/memory/sessions/{session_id}/memory_refs.json";

    // docs/ — agent-written markdown documents (research, reports, drafts, notes)
    public const string LocalDocs = ".fuseraft/docs";

    // knowledge/ — durable cross-session knowledge (ADRs, repository memory, objectives)
    public const string LocalKnowledge          = ".fuseraft/knowledge";
    public const string LocalDecisions          = ".fuseraft/knowledge/decisions";
    public const string LocalDecisionsArchive   = ".fuseraft/knowledge/decisions/archive";
    public const string LocalRepositoryMemory   = ".fuseraft/knowledge/repository";
    public const string LocalObjectives         = ".fuseraft/knowledge/objectives";
    public const string LocalLifecycleConfig    = ".fuseraft/knowledge/lifecycle.yaml";
    public const string LocalProvenanceArchive  = ".fuseraft/state/provenance.archive.json";

    // Repository semantic graph — nodes + edges for all symbols in the project.
    public const string LocalRepositoryGraph = ".fuseraft/state/repository.graph";

    // Architecture drift detection — user-authored layer manifest.
    public const string LocalArchitectureManifest = ".fuseraft/architecture.yaml";

    // checkpoints/ — session checkpoint files written when Checkpoint.Mode is set
    public const string LocalCheckpoints = ".fuseraft/checkpoints";

    // tests/ — tester-created test scripts and fixture files (any language/format)
    public const string LocalTests        = ".fuseraft/tests";
    public const string LocalTestFixtures = ".fuseraft/tests/fixtures";

    // Already-subdirectorized paths (unchanged locations)
    public const string LocalContext   = ".fuseraft/context";

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
    /// <summary>
    /// Returns a runtime environment block injected into every agent system prompt so agents
    /// know the OS, architecture, shell, working directory, and current date/time without
    /// having to infer or probe for them.
    /// </summary>
    public static string BuildOsEnvironmentBlock()
    {
        string os, shell;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            os    = "Windows";
            shell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            os    = "macOS";
            shell = Environment.GetEnvironmentVariable("SHELL") ?? "zsh";
        }
        else
        {
            os    = "Linux";
            shell = Environment.GetEnvironmentVariable("SHELL") ?? "bash";
        }

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86   => "x86",
            Architecture.Arm   => "arm",
            _                  => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        var now = DateTimeOffset.Now;
        var tz  = TimeZoneInfo.Local.Id;
        var cwd = Directory.GetCurrentDirectory();

        return new System.Text.StringBuilder()
            .AppendLine("## Runtime Environment")
            .AppendLine($"OS: {os}")
            .AppendLine($"Architecture: {arch}")
            .AppendLine($"Shell: {shell}")
            .AppendLine($"Working directory: {cwd}")
            .Append(    $"Date/time: {now:yyyy-MM-dd HH:mm:ss zzz} ({tz})")
            .ToString();
    }

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
        sb.AppendLine($"  {LocalIntents,-42} — in-progress intent records (consult before repeating work)");
        sb.AppendLine($"  {LocalSessionContext,-42} — shared handoff notes (read at turn start; write before handoff)");
        sb.AppendLine("  .fuseraft/state/evidence.json             — structured evidence graph");
        sb.AppendLine("  .fuseraft/state/file_versions.json        — per-file versioned write counters");
        sb.AppendLine($"  {LocalBrief,-42} — task brief (if present)");
        sb.AppendLine($"  {LocalBrownfieldBrief,-42} — brownfield discovery brief (if present)");
        sb.AppendLine("  .fuseraft/artifacts/test-report.json      — tester output / validator input (if present)");
        sb.AppendLine($"  {LocalConventions,-42} — brownfield convention profile (if present)");
        sb.AppendLine($"  {LocalChatroom,-42} — cross-agent chatroom messages (if present)");
        sb.AppendLine("  .fuseraft/docs/                           — write all markdown notes, reports, and drafts here");
        sb.AppendLine("  .fuseraft/tests/                          — write all test scripts and test support files here");
        sb.AppendLine("  .fuseraft/tests/fixtures/                 — seed data, stubs, and fixture files");
        sb.AppendLine("  .fuseraft/context/                        — injected reference documents (see .fuseraft/context/index.json)");
        sb.AppendLine("  .fuseraft/summaries/                      — compaction summaries");
        sb.AppendLine("  .fuseraft/knowledge/decisions/            — architecture decision records (use decision_search / decision_read)");
        sb.Append(    "  .fuseraft/state/repository.graph          — repository semantic graph (use graph_search / graph_refs / graph_dependents)");
        return sb.ToString();
    }
}
