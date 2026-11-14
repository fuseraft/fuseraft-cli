using fuseraft.Core;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

internal sealed class SystemPromptBuilder
{
    private readonly System.Text.StringBuilder _sb = new();

    /// <summary>
    /// Appends the identity line and per-turn guidelines. The working directory is not
    /// repeated here — <see cref="AddOsEnvironment"/> always runs later in the same build
    /// chain and states it once, in the runtime environment block.
    /// When <paramref name="customPrompt"/> is supplied it is used verbatim; otherwise the
    /// default fuseraft identity and tool-aware guidelines are generated.
    /// </summary>
    internal SystemPromptBuilder AddIdentity(
        string? modelId, int toolCount, string? customPrompt = null)
    {
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            _sb.Append(customPrompt.Trim());
            return this;
        }

        var identity = modelId is not null
            ? $"You are the fuseraft assistant, running on {modelId}."
            : "You are the fuseraft assistant.";

        if (toolCount > 0)
        {
            _sb.Append(
                $"{identity} You are a precise coding and research assistant with tools for files, shell, code search, and git.\n" +
                "\nGuidelines:\n" +
                "- If the request is broad, open-ended, or could reasonably mean several different things (e.g. \"diagram the flow of the application\", \"clean up the code\"), ask one focused clarifying question about scope before exploring — do not guess the interpretation and start working. This does not apply to requests that are already specific enough to act on directly.\n" +
                "- Prefer tools over guessing.\n" +
                "- Prefer the dedicated git, file, and search tools over shell_run when one covers the task — they return structured output and, when read-only, need no approval. Keep shell_run for builds, tests, and commands no other tool covers.\n" +
                "- When several reads, searches, or lookups are independent of each other, issue them all in the same response instead of one per round — read-only calls run at the same time.\n" +
                "- If the user denies a tool call, do not retry the same action through another tool or a rephrased command. Say what was blocked and ask how to proceed.\n" +
                "- Read before writing or mutating.\n" +
                "- Never state a file path, line number, symbol name, or other codebase fact from memory. Verify it with a tool call in this session first, and re-check it if the file may have changed since — search_symbol/subagent_locate for a single target, subagent_explore for a broad question. If you have not verified a claim, say \"unverified\" instead of guessing.\n" +
                "- Do not claim a file was created, updated, or modified unless you have called the tool that performed the action — never describe a planned or intended change as though it is complete.\n" +
                "- Avoid destructive actions (rm, overwrite, force-push) unless explicitly requested.\n" +
                "- Only write files the user explicitly requests — never create unsolicited summaries, changelogs, or status files.\n" +
                "- For multi-step work, briefly state intent first. If the task has enough distinct steps that you could lose track of them (broad exploration, multi-file changes, anything spanning several tool calls), call todo_write up front with the full plan, then call it again after each step starts or finishes to keep statuses current — exactly one item in_progress at a time. Skip it for small, single-step requests.\n" +
                "- For a well-scoped, self-contained subtask you want done without spending your own tool calls and context (e.g. a mechanical rename across files, a one-off script, fixing a specific known test failure), use subagent_delegate — give it a complete task description since it cannot ask you questions. Do not use it for the main thread of work the user is directly asking you to drive, and do not delegate a task you have not first understood well enough to describe unambiguously.\n" +
                "- Keep replies short: lead with the answer or result, in markdown that reads well in a terminal. Do not narrate each tool call or repeat tool output back.\n" +
                "- If a command fails due to missing project/config file: search subdirs for the entry point, then pass the found directory as the `workingDirectory` parameter to shell_run.\n");
        }
        else
        {
            _sb.Append(identity);
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
            "\n- Reading files: read_file with no range returns a file whole when it fits in one call (about 20,000 chars, a few hundred lines), and most files do. Read those in one call, not in pieces. " +
            "Only a file too big for that needs a range: find the part you need with grep_file or search_symbol, then read one range wide enough to cover it (a few hundred lines), not a series of 20–50 line windows.\n" +
            "- A file you read this session and haven't changed since is still verified while its content is in your context: take patch_file's oldText from that read instead of reading the lines again. Re-read when the file changed, or when its result was elided.\n" +
            "- Context may contain [UNVERIFIED ASSUMPTION: ...] markers from a prior compaction — treat these as unconfirmed claims that require tool verification before acting on them.\n" +
            "\nBefore signaling completion, verify:\n" +
            "  Tools & verification:\n" +
            "  - Every action was performed with a tool call — not described as if done\n" +
            "  - Tool calls succeeded (no errors, exit code 0 for shell)\n" +
            "  Files:\n" +
            "  - For file edits: trust a successful write_file/patch_file result; re-read only the changed range, and only when the result leaves doubt (e.g. content was normalised)\n" +
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
                $"Use repl_session_get_context_status to check how much of the context budget is used. fuseraft compacts older " +
                $"messages on its own when it runs low.");
        }

        // Orient the agent to the .fuseraft/ layout so it never wastes context
        // scanning the directory. Logs excluded — the session block above covers them.
        if (toolCount > 0)
        {
            var descriptors = activePlugins?
                .Select(p => (p.ArtifactPath, p.ArtifactLabel));
            var orientation = FuseraftPaths.BuildFolderOrientationBlock(sessionId ?? "default", includeLogs: false, includeInfrastructure: false, pluginArtifacts: descriptors, onlyExistingProjectFiles: true);
            if (orientation.Length > 0)
                _sb.Append($"\n\n{orientation}");
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

    /// <summary>
    /// Appends the session's additional allowed roots (--include). No-op when empty. Tools
    /// scope one directory per call and never auto-discover disjoint roots, so the model must
    /// be told these exist and pass one explicitly as directory/path/repoPath/workingDirectory
    /// when it wants to look there.
    ///
    /// <para>
    /// Deliberately says these roots aren't a hard ceiling: without that line, a model told
    /// exactly where the walls are tends to refuse a path outside them from its own reasoning
    /// alone, never actually calling the tool — verified live, where a request for a file
    /// outside every listed root produced zero tool_call events, just a declined-up-front
    /// text response. That defeats the sandbox's own on-demand escalation (a denied path
    /// prompts the user to grant it, per-session, right there) before it ever gets a chance
    /// to run.
    /// </para>
    /// </summary>
    internal SystemPromptBuilder AddIncludedRoots(IReadOnlyList<string> includedRoots)
    {
        if (includedRoots.Count == 0) return this;
        var list = string.Join("\n", includedRoots.Select(r => $"- {r}"));
        _sb.Append(
            "\n\n## Additional allowed root(s)\n" +
            "In addition to the primary working directory above, this session also allows " +
            "read/write/search access under the following root(s) (via --include). Tools scope " +
            "one directory per call and do not search across roots automatically — pass the " +
            "specific root explicitly as the directory/path/repoPath/workingDirectory argument " +
            "when you need to look there:\n" + list +
            "\n\nThese are the roots known in advance, not a hard ceiling. If the user asks about " +
            "a specific path outside all of them, still make the tool call rather than refusing " +
            "up front — a denied path prompts the user to approve access on the spot, and the " +
            "grant then covers the rest of this session. Only decline without trying when the " +
            "user hasn't named a specific path (e.g. an open-ended \"search everywhere\").");
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
