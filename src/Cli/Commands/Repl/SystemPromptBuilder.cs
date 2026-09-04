using fuseraft.Core;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

internal sealed class SystemPromptBuilder
{
    private readonly System.Text.StringBuilder _sb = new();

    /// <summary>
    /// Appends the identity line, working directory, and per-turn guidelines.
    /// When <paramref name="customPrompt"/> is supplied it is used verbatim (with CWD appended);
    /// otherwise the default fuseraft identity and tool-aware guidelines are generated.
    /// </summary>
    internal SystemPromptBuilder AddIdentity(
        string? modelId, string cwd, int toolCount, string? customPrompt = null)
    {
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            _sb.Append(customPrompt.Trim());
            _sb.Append($"\n\nThe current working directory is: {cwd}.");
            return this;
        }

        var identity = modelId is not null
            ? $"You are the fuseraft assistant, running on {modelId}."
            : "You are the fuseraft assistant.";

        if (toolCount > 0)
        {
            _sb.Append(
                $"{identity} You are a precise coding and research assistant with tools for files, shell, code search, and git.\n" +
                $"\nCurrent working directory: {cwd}\n" +
                "\nGuidelines:\n" +
                "- If the request is broad, open-ended, or could reasonably mean several different things (e.g. \"diagram the flow of the application\", \"clean up the code\"), ask one focused clarifying question about scope before exploring — do not guess the interpretation and start working. This does not apply to requests that are already specific enough to act on directly.\n" +
                "- Prefer tools over guessing.\n" +
                "- Read before writing or mutating.\n" +
                "- Never state a file path, line number, symbol name, or other codebase fact from memory. Verify it with a tool call in this turn first — search_symbol/sub_agent_locate for a single target, sub_agent_explore for a broad question. If you have not verified a claim, say \"unverified\" instead of guessing.\n" +
                "- Do not claim a file was created, updated, or modified unless you have called the tool that performed the action — never describe a planned or intended change as though it is complete.\n" +
                "- Avoid destructive actions (rm, overwrite, force-push) unless explicitly requested.\n" +
                "- Only write files the user explicitly requests — never create unsolicited summaries, changelogs, or status files.\n" +
                "- For multi-step work, briefly state intent first. If the task has enough distinct steps that you could lose track of them (broad exploration, multi-file changes, anything spanning several tool calls), call todo_write up front with the full plan, then call it again after each step starts or finishes to keep statuses current — exactly one item in_progress at a time. Skip it for small, single-step requests.\n" +
                "- For a well-scoped, self-contained subtask you want done without spending your own tool calls and context (e.g. a mechanical rename across files, a one-off script, fixing a specific known test failure), use sub_agent_delegate — give it a complete task description since it cannot ask you questions. Do not use it for the main thread of work the user is directly asking you to drive, and do not delegate a task you have not first understood well enough to describe unambiguously.\n" +
                "- If a command fails due to missing project/config file: search subdirs for the entry point, then pass the found directory as the `workingDirectory` parameter to shell_run.\n");
        }
        else
        {
            _sb.Append($"{identity} The current working directory is: {cwd}.");
        }

        return this;
    }

    /// <summary>
    /// Appends large-file read discipline and the pre-completion verification checklist.
    /// No-op when <paramref name="toolCount"/> is zero. Applied even when a custom identity
    /// prompt was set so all deployments receive the guardrails.
    /// </summary>
    internal SystemPromptBuilder AddToolGuidance(int toolCount)
    {
        if (toolCount == 0) return this;

        _sb.Append(
            "\n- For large files: call get_file_summary first (shows first 30 lines and file size), grep_file to locate the relevant section, then read_file with startLine/maxLines for that section only — never cold-read a large file in full.\n" +
            "- Context may contain [UNVERIFIED ASSUMPTION: ...] markers from a prior compaction — treat these as unconfirmed claims that require tool verification before acting on them.\n" +
            "\nBefore signaling completion, verify:\n" +
            "  Tools & verification:\n" +
            "  - Every action was performed with a tool call — not described as if done\n" +
            "  - Tool calls succeeded (no errors, exit code 0 for shell)\n" +
            "  Files:\n" +
            "  - For file writes: re-read the file to confirm content is correct\n" +
            "  Shell:\n" +
            "  - Shell output is shown; it confirms the goal was met\n" +
            "  Completeness:\n" +
            "  - Every part of the user's request has been addressed\n" +
            "  - Nothing was deferred or skipped without explaining why\n" +
            "  If any check fails, complete it before responding.\n");

        return this;
    }

    /// <summary>
    /// Appends the current session metadata block and, when tools are enabled, the
    /// <c>~/.fuseraft/</c> folder orientation map so the agent never scans for artifacts.
    /// </summary>
    internal SystemPromptBuilder AddSessionInfo(
        string? sessionId, DateTime? startedAt, string cwd, int toolCount,
        IEnumerable<IHasArtifact>? activePlugins = null)
    {
        if (sessionId is not null)
        {
            var snapshotPath = Path.Combine(FuseraftPaths.GlobalReplSessions, $"repl-{sessionId}.json");
            var sessionStarted = startedAt.HasValue
                ? startedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                : "unknown";
            _sb.Append(
                $"\n\n# Current session\n" +
                $"Session ID: {sessionId}\n" +
                $"Started:    {sessionStarted}\n" +
                $"Snapshot:   {snapshotPath}\n" +
                $"Event log:  {FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalReplEventsLog, sessionId, FuseraftPaths.ProjectSlug(cwd))}\n" +
                $"Use the repl_session_* tools to inspect session metadata, list past sessions, or read log files.");
        }

        // Orient the agent to the .fuseraft/ layout so it never wastes context
        // scanning the directory. Logs excluded — the session block above covers them.
        if (toolCount > 0)
        {
            var descriptors = activePlugins?
                .Select(p => (p.ArtifactPath, p.ArtifactLabel));
            _sb.Append($"\n\n{FuseraftPaths.BuildFolderOrientationBlock(sessionId ?? "default", includeLogs: false, includeInfrastructure: false, pluginArtifacts: descriptors)}");
        }

        return this;
    }

    /// <summary>
    /// Reads <c>AGENTS.md</c> from <paramref name="cwd"/> and appends it as project instructions.
    /// No-op when the file is absent or empty.
    /// </summary>
    internal SystemPromptBuilder AddProjectInstructions(string cwd)
    {
        var block = ReadAgentsMd(cwd);
        if (block is not null)
            _sb.Append($"\n\n{block}");
        return this;
    }

    /// <summary>Appends the OS/runtime environment block (OS, arch, shell, CWD, date/time).</summary>
    internal SystemPromptBuilder AddOsEnvironment()
    {
        _sb.Append($"\n\n{FuseraftPaths.BuildOsEnvironmentBlock()}");
        return this;
    }

    /// <summary>Appends the REPL memory block. No-op when <paramref name="memoryBlock"/> is null.</summary>
    internal SystemPromptBuilder AddMemory(string? memoryBlock)
    {
        if (memoryBlock is not null)
            _sb.Append($"\n\n{memoryBlock}");
        return this;
    }

    /// <summary>Appends the skills catalog. No-op when <paramref name="skillsCatalog"/> is null.</summary>
    internal SystemPromptBuilder AddSkills(string? skillsCatalog)
    {
        if (skillsCatalog is not null)
            _sb.Append($"\n\n{skillsCatalog}");
        return this;
    }

    internal string Build() => _sb.ToString();

    private static string? ReadAgentsMd(string cwd)
    {
        var path = Path.Combine(cwd, "AGENTS.md");
        if (!File.Exists(path)) return null;
        try
        {
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(content)
                ? null
                : $"# Project instructions (from AGENTS.md)\n\n{content}";
        }
        catch { return null; }
    }
}
