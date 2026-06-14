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

    public static string GlobalSkillsIndex              => Path.Combine(GlobalRoot, "skills", "index.db");
    public static string GlobalSkillCurationLog         => Path.Combine(GlobalRoot, "skill-curation.jsonl");
    public static string GlobalSchedule                 => Path.Combine(GlobalRoot, "schedule");
    public static string GlobalMemoryRepl               => Path.Combine(GlobalRoot, "memory", "repl");
    public static string GlobalMemoryAgent(string name) => Path.Combine(GlobalRoot, "memory", "agents", name);

    // ── Project-local (.fuseraft/ relative to CWD) — user-authored, all tracked by git ──

    // artifacts/ — non-session-scoped outputs (local, agent-generated per run)
    public const string LocalTestReport           = ".fuseraft/artifacts/test-report.json";
    public const string LocalAuditFindings        = ".fuseraft/artifacts/audit-findings.json";
    public const string LocalRemediationPlan      = ".fuseraft/artifacts/remediation-plan.json";
    public const string LocalOpsPlan              = ".fuseraft/artifacts/ops-plan.yaml";

    // data/ — data engineering outputs (local, agent-generated per run)
    public const string LocalDataRoot             = ".fuseraft/data";
    public const string LocalDataManifest         = ".fuseraft/data/manifest.json";
    public const string LocalDataAnalysisResults  = ".fuseraft/data/analysis-results.json";

    // docs/ (supplemental) — structured review artifacts
    public const string LocalResearchFindings     = ".fuseraft/docs/research-findings.md";
    public const string LocalResearchReview       = ".fuseraft/docs/research-review.json";
    public const string LocalDebatePosition       = ".fuseraft/docs/position.md";
    public const string LocalDebateSummary        = ".fuseraft/docs/debate-summary.md";
    public const string LocalDebateVerdict        = ".fuseraft/docs/verdict.md";

    // ── Global project-scoped runtime paths (~/.fuseraft/) — keyed by {project_slug} ──
    // These are templates; expand with ExpandProjectPaths(path, slug) or
    // ExpandSessionPaths(path, sessionId, slug). ExpandSessionId also auto-expands
    // {project_slug} from CWD so existing callers work without change.

    // logs/ — project diagnostics (not session-specific)
    public const string LocalLogs                 = "~/.fuseraft/logs/{project_slug}";
    public const string LocalReplEventsLog        = "~/.fuseraft/logs/{project_slug}/repl_events.jsonl";
    public const string LocalProviderErrors       = "~/.fuseraft/logs/{project_slug}/provider_errors.jsonl";
    public const string LocalAppLog               = "~/.fuseraft/logs/{project_slug}/app.log";

    // state/ — cross-session mutable runtime state
    public const string LocalState                = "~/.fuseraft/state/{project_slug}";
    public const string LocalChanges              = "~/.fuseraft/state/{project_slug}/changes.json";
    public const string LocalEvidence             = "~/.fuseraft/state/{project_slug}/evidence.json";
    public const string LocalProvenance           = "~/.fuseraft/state/{project_slug}/provenance.json";
    public const string LocalFileVersions         = "~/.fuseraft/state/{project_slug}/file_versions.json";
    public const string LocalKnowledgeFindings    = "~/.fuseraft/state/{project_slug}/knowledge_findings.json";
    public const string LocalProvenanceArchive    = "~/.fuseraft/state/{project_slug}/provenance.archive.json";
    public const string LocalRepositoryGraph      = "~/.fuseraft/state/{project_slug}/repository.graph";
    public const string LocalExecutionState       = "~/.fuseraft/state/{project_slug}/execution-state.json";
    public const string LocalInvestigationLog     = "~/.fuseraft/state/{project_slug}/investigation-log.json";

    // sessions/ — all session-scoped runtime data, keyed by {project_slug}/{session_id}
    public const string LocalSessions             = "~/.fuseraft/sessions/{project_slug}";
    public const string LocalEventsLog            = "~/.fuseraft/sessions/{project_slug}/{session_id}/events.jsonl";
    public const string LocalIntents              = "~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json";
    public const string LocalSessionContext       = "~/.fuseraft/sessions/{project_slug}/{session_id}/context_summary.md";
    public const string LocalSessionReadCache     = "~/.fuseraft/sessions/{project_slug}/{session_id}/read_cache.json";
    public const string LocalSessionToolArtifacts = "~/.fuseraft/sessions/{project_slug}/{session_id}/tool-results";
    public const string LocalBrief                = "~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json";
    public const string LocalConventions          = "~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json";
    public const string LocalBrownfieldBrief      = "~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json";
    public const string LocalBriefReview          = "~/.fuseraft/sessions/{project_slug}/{session_id}/brief-review.json";
    public const string LocalChatroom             = "~/.fuseraft/sessions/{project_slug}/{session_id}/chatroom.jsonl";
    public const string LocalSessionScratchpad    = "~/.fuseraft/sessions/{project_slug}/{session_id}/scratchpad";
    public const string LocalMemoryRefs           = "~/.fuseraft/sessions/{project_slug}/{session_id}/memory_refs.json";
    public const string LocalCtxViz               = "~/.fuseraft/sessions/{project_slug}/{session_id}/ctx_viz.html";

    // ── Global session log templates ──────────────────────────────────────────
    // Session logs (events + ctx_snapshots) live under ~/.fuseraft/logs/sessions/
    // organised as {project_slug}/{session_id}/ so all projects share one root and
    // sessions are trivially filterable by project without scanning content.

    /// <summary>
    /// Template for the per-session event log under the global fuseraft home.
    /// Call <see cref="ExpandSessionPaths"/> to resolve both tokens.
    /// </summary>
    public const string GlobalEventsLogTemplate =
        "~/.fuseraft/logs/sessions/{project_slug}/{session_id}/events.jsonl";

    /// <summary>
    /// Template for the per-session context-window snapshot log under the global fuseraft home.
    /// Call <see cref="ExpandSessionPaths"/> to resolve both tokens.
    /// </summary>
    public const string GlobalCtxSnapshotsTemplate =
        "~/.fuseraft/logs/sessions/{project_slug}/{session_id}/ctx_snapshots.jsonl";

    /// <summary>
    /// Template for the per-session postmortem snapshot directory written when --snapshot is passed.
    /// Contains turns.jsonl (per-turn records) and manifest.json (run summary).
    /// Call <see cref="ExpandSessionPaths"/> to resolve both tokens.
    /// </summary>
    public const string GlobalPostmortemSnapshotTemplate =
        "~/.fuseraft/snapshots/{project_slug}/{session_id}";

    /// <summary>
    /// Converts an absolute project path to a filesystem-safe slug used as the
    /// project subdirectory under <c>~/.fuseraft/logs/sessions/</c>.
    /// Example: <c>/home/scs/github/fuseraft/brewer</c> → <c>home-scs-github-fuseraft-brewer</c>
    /// </summary>
    public static string ProjectSlug(string absolutePath)
    {
        var path = absolutePath;
        // Strip Windows drive letter ("C:") before normalising separators.
        if (path.Length >= 2 && path[1] == ':')
            path = path[2..];
        return path
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar,    '-')
            .Replace(Path.AltDirectorySeparatorChar, '-')
            .ToLowerInvariant();
    }

    /// <summary>
    /// Returns the per-project sessions directory under the global fuseraft home.
    /// All session artifact directories for a project live here.
    /// </summary>
    public static string GlobalProjectSessions(string slug) => Path.Combine(GlobalRoot, "sessions", slug);

    /// <summary>
    /// Expands <c>{session_id}</c> in a path. When the path also contains
    /// <c>{project_slug}</c> (runtime-artifact templates), the token is resolved
    /// from <see cref="Directory.GetCurrentDirectory"/> automatically so callers
    /// that only know the session ID continue to work without change.
    /// Also expands a leading <c>~</c> to the user home directory.
    /// </summary>
    public static string ExpandSessionId(string path, string sessionId)
    {
        var result = path.Replace("{session_id}", sessionId, StringComparison.Ordinal);
        if (result.Contains("{project_slug}"))
            result = result.Replace("{project_slug}", ProjectSlug(Directory.GetCurrentDirectory()), StringComparison.Ordinal);
        return result.StartsWith("~/") || result == "~" ? ExpandPath(result) : result;
    }

    /// <summary>
    /// Expands <c>{session_id}</c>, <c>{project_slug}</c>, and a leading <c>~</c> in a path.
    /// Use this for any path that may contain either global-template token.
    /// </summary>
    public static string ExpandSessionPaths(string path, string sessionId, string projectSlug) =>
        ExpandPath(
            path.Replace("{session_id}",   sessionId,   StringComparison.Ordinal)
                .Replace("{project_slug}", projectSlug, StringComparison.Ordinal));

    /// <summary>
    /// Replaces <c>{session_id}</c>, <c>{project_slug}</c>, and <c>~/</c> tokens inside
    /// arbitrary text (e.g. agent Instructions). Unlike <see cref="ExpandSessionPaths"/>,
    /// this does <em>not</em> call <c>Path.GetFullPath</c>, which would prepend the CWD to
    /// the entire multi-line string and corrupt it.
    /// </summary>
    public static string ExpandTextTokens(string text, string sessionId, string projectSlug)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return text
            .Replace("{session_id}",   sessionId,   StringComparison.Ordinal)
            .Replace("{project_slug}", projectSlug, StringComparison.Ordinal)
            .Replace("~/",             home + "/",  StringComparison.Ordinal);
    }

    /// <summary>
    /// Expands <c>{project_slug}</c> and a leading <c>~</c> in a path.
    /// Use for project-scoped runtime paths that have no <c>{session_id}</c> token.
    /// </summary>
    public static string ExpandProjectPaths(string path, string projectSlug) =>
        ExpandPath(path.Replace("{project_slug}", projectSlug, StringComparison.Ordinal));

    // docs/ — agent-written markdown documents (research, reports, drafts, notes)
    public const string LocalDocs = ".fuseraft/docs";

    // knowledge/ — durable cross-session knowledge (ADRs, repository memory, objectives)
    // knowledge/repository/ (agent-managed hashes) is global; the rest are user-authored and local.
    public const string LocalKnowledge          = ".fuseraft/knowledge";
    public const string LocalDecisions          = ".fuseraft/knowledge/decisions";
    public const string LocalDecisionsArchive   = ".fuseraft/knowledge/decisions/archive";
    public const string LocalRepositoryMemory   = "~/.fuseraft/knowledge/{project_slug}/repository";
    public const string LocalObjectives         = ".fuseraft/knowledge/objectives";
    public const string LocalLifecycleConfig    = ".fuseraft/knowledge/lifecycle.yaml";

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

    /// <param name="includeInfrastructure">
    /// When true (default), includes paths for orchestration-only artifacts: intent records,
    /// evidence graph, file version counters, briefs, test report, and conventions.
    /// Pass false in REPL sessions where these orchestration artifacts do not exist.
    /// </param>
    /// <param name="pluginArtifacts">
    /// Resolved artifact paths for the plugins actually loaded in this context.
    /// When non-null, only these entries are shown for the plugin-backed paths; when null
    /// and <paramref name="includeInfrastructure"/> is true, the full hardcoded list is used
    /// as a fallback so existing callers that have not adopted per-agent filtering still work.
    /// </param>
    public static string BuildFolderOrientationBlock(
        string sessionId,
        bool includeLogs = true,
        bool includeInfrastructure = true,
        IEnumerable<(string Path, string Label)>? pluginArtifacts = null)
    {
        var slug = ProjectSlug(Directory.GetCurrentDirectory());

        string Expand(string template) => ExpandSessionPaths(template, sessionId, slug);
        string ExpandP(string template) => ExpandProjectPaths(template, slug);

        var sb = new System.Text.StringBuilder();

        // Collect artifact entries first; only emit the header if there is something to list.
        var artifacts = new System.Text.StringBuilder();
        if (includeLogs)
        {
            artifacts.AppendLine($"  {Expand(LocalEventsLog),-70} — agent/orchestration event log (JSONL)");
            artifacts.AppendLine($"  {ExpandP(LocalReplEventsLog),-70} — REPL event log (JSONL)");
            artifacts.AppendLine($"  {ExpandP(LocalAppLog),-70} — application log");
        }

        // Orchestration-only infrastructure paths — always shown for orchestrator agents.
        if (includeInfrastructure)
        {
            artifacts.AppendLine($"  {Expand(LocalIntents),-70} — in-progress intent records (consult before repeating work)");
            artifacts.AppendLine($"  {ExpandP(LocalEvidence),-70} — structured evidence graph");
            artifacts.AppendLine($"  {ExpandP(LocalFileVersions),-70} — per-file versioned write counters");
            artifacts.AppendLine($"  {Expand(LocalBrief),-70} — task brief (if present)");
            artifacts.AppendLine($"  {Expand(LocalBrownfieldBrief),-70} — brownfield discovery brief (if present)");
            artifacts.AppendLine($"  {LocalTestReport,-70} — tester output / validator input (if present)");
            artifacts.AppendLine($"  {Expand(LocalConventions),-70} — brownfield convention profile (if present)");
        }

        // Plugin artifact paths: per-agent collection when available, otherwise hardcoded fallback.
        if (pluginArtifacts is not null)
        {
            foreach (var (path, label) in pluginArtifacts)
                artifacts.AppendLine($"  {path,-70} — {label}");
        }
        else if (includeInfrastructure)
        {
            artifacts.AppendLine($"  {ExpandP(LocalChanges),-70} — tool-call change log");
            artifacts.AppendLine($"  {Expand(LocalSessionContext),-70} — shared handoff notes (read at turn start; write before handoff)");
            artifacts.AppendLine($"  {Expand(LocalChatroom),-70} — cross-agent chatroom messages (if present)");
            artifacts.AppendLine($"  {Expand(LocalSessionScratchpad),-70} — agent scratchpad files (session-scoped)");
        }

        if (artifacts.Length > 0)
        {
            sb.AppendLine("## Runtime artifacts — all stored globally under ~/.fuseraft/ (do not scan)");
            sb.AppendLine("Reference these paths directly when needed:");
            sb.Append(artifacts);
        }

        sb.AppendLine("## User-authored project files — tracked by git (in .fuseraft/)");
        sb.AppendLine("  .fuseraft/docs/                 — write all markdown notes, reports, and drafts here");
        sb.AppendLine("  .fuseraft/tests/                — write all test scripts and test support files here");
        sb.AppendLine("  .fuseraft/tests/fixtures/       — seed data, stubs, and fixture files");
        sb.AppendLine("  .fuseraft/context/              — injected reference documents (see .fuseraft/context/index.json)");
        sb.AppendLine("  .fuseraft/summaries/            — compaction summaries");
        sb.AppendLine("  .fuseraft/knowledge/decisions/  — architecture decision records (use decision_search / decision_read)");
        sb.Append(    $"  {ExpandP(LocalRepositoryGraph),-70} — repository semantic graph (use graph_search / graph_refs / graph_dependents)");
        return sb.ToString();
    }
}
