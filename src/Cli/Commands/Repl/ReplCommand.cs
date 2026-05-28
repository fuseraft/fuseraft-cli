using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Display;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

public sealed class ReplSettings : CommandSettings
{
    [CommandOption("-m|--model")]
    [Description("Model ID to use (e.g. gpt-4o, claude-sonnet-4-6, grok-4). Overrides ~/.fuseraft/config if set.")]
    public string? Model { get; set; }

    [CommandOption("-s|--system")]
    [Description("System prompt for the REPL session.")]
    public string? SystemPrompt { get; set; }

    [CommandOption("--no-banner")]
    [Description("Skip the Figlet banner.")]
    public bool NoBanner { get; set; }

    [CommandOption("--no-tools")]
    [Description("Disable all built-in tools (FileSystem, Shell, Search, Git, Http).")]
    public bool NoTools { get; set; }

    [CommandOption("--verbose")]
    [Description("Show debug-level log output.")]
    public bool Verbose { get; set; }

    [CommandOption("--resume")]
    [Description("Resume a previous REPL session by ID (e.g. --resume abc123ef).")]
    public string? Resume { get; set; }

    [CommandOption("--vscode")]
    [Description("Run in VS Code webview mode (JSON bridge over stdio). Set globally by Program.cs pre-parse; declared here so Spectre does not reject it as an unknown flag.")]
    public bool VsCode { get; set; }
}

public sealed class ReplCommand(ILoggerFactory loggerFactory) : AsyncCommand<ReplSettings>
{
    private static readonly (string EnvVar, string ModelId)[] AutoDetectOrder =
    [
        ("ANTHROPIC_API_KEY",  "claude-sonnet-4-5"),
        ("OPENAI_API_KEY",     "gpt-4o-mini"),
        ("XAI_API_KEY",        "grok-4-1-fast-reasoning"),
        ("GOOGLE_AI_API_KEY",  "gemini-2.0-flash"),
        ("MISTRAL_API_KEY",    "mistral-small-latest"),
        ("DEEPSEEK_API_KEY",   "deepseek-chat"),
    ];

    protected override async Task<int> ExecuteAsync(
        CommandContext context, ReplSettings settings, CancellationToken cancellationToken)
    {
        // JSON bridge mode: active when launched from the VS Code webview panel
        // (--vscode + stdin redirected from the extension's child process).
        bool jsonMode = OrchestratorBuilder.VsCodeMode && Console.IsInputRedirected;

        var keyStore = ApiKeyStoreFactory.Create();
        var (userCfg, legacyKey) = UserConfigStore.Load();

        if (OrchestratorBuilder.VsCodeMode)
        {
            // Running from VS Code. Prefer an API key explicitly injected by the
            // extension (FUSERAFT_API_KEY), then fall back to any legacy plaintext
            // key still in the config file, then to the OS keychain.  The env-var
            // path exists for future use; most users will hit the keychain fallback.
            if (userCfg is not null)
            {
                var envKey = Environment.GetEnvironmentVariable("FUSERAFT_API_KEY");
                userCfg.ApiKey = !string.IsNullOrEmpty(envKey)
                    ? envKey
                    : !string.IsNullOrEmpty(legacyKey)
                        ? legacyKey
                        : await keyStore.RetrieveAsync() ?? string.Empty;
            }
        }
        else if (!string.IsNullOrEmpty(legacyKey))
        {
            await keyStore.StoreAsync(legacyKey);
            userCfg!.ApiKey = legacyKey;
            UserConfigStore.Save(userCfg);
            AnsiConsole.MarkupLine($"[dim]API key migrated to {Markup.Escape(keyStore.StoreName)}.[/]");
        }
        else if (userCfg is not null)
        {
            userCfg.ApiKey = await keyStore.RetrieveAsync() ?? string.Empty;
        }

        var modelId = ResolveModelId(settings, userCfg);

        bool pendingSave = false;
        if (userCfg == null || !userCfg.IsConfigured)
        {
            if (jsonMode)
            {
                ReplJsonBridge.Emit(new { type = "error", text = "fuseraft is not configured. Run 'fuseraft setup' or use the fuseraft: Setup command in VS Code." });
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]No configuration found at[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
            AnsiConsole.WriteLine();
            string? wizardKey;
            (userCfg, wizardKey) = ReplFactory.RunSetupWizard(modelId, userCfg);
            if (userCfg is null || wizardKey is null) return 1;
            await keyStore.StoreAsync(wizardKey);
            userCfg.ApiKey = wizardKey;
            modelId        = userCfg.ModelId;
            pendingSave    = true;
        }

        if (string.IsNullOrEmpty(modelId))
        {
            if (jsonMode)
                ReplJsonBridge.Emit(new { type = "error", text = "No model specified and no supported API key found. Run fuseraft setup to configure." });
            else
            {
                AnsiConsole.MarkupLine("[red]✗ No model specified and no supported API key found.[/]");
                AnsiConsole.MarkupLine("[dim]Run[/] [bold]fuseraft repl[/] [dim]to configure, or pass[/] [bold]--model[/].");
            }
            return 1;
        }

        var modelConfig = ReplFactory.BuildModelConfig(modelId, userCfg);
        using var factory = new ChatClientFactory();

        var toolsByCategory = new Dictionary<string, List<AIFunction>>(StringComparer.OrdinalIgnoreCase);
        using ShellPlugin? shellPlugin = settings.NoTools ? null : new ShellPlugin(shellPolicy: TryLoadDefaultShellPolicy());
        SubAgentPlugin? subAgent = null;
        SkillsPlugin?   skillsPlugin   = null;
        string?         skillsCatalog  = null;
        if (!settings.NoTools)
        {
            toolsByCategory["FileSystem"] = PluginRegistry.GetFunctionsFromObject(new FileSystemPlugin()).ToList();
            toolsByCategory["Shell"]      = PluginRegistry.GetFunctionsFromObject(shellPlugin!).ToList();
            toolsByCategory["Search"]     = PluginRegistry.GetFunctionsFromObject(new SearchPlugin()).ToList();
            toolsByCategory["Git"]        = PluginRegistry.GetFunctionsFromObject(new GitPlugin()).ToList();
            toolsByCategory["Http"]       = PluginRegistry.GetFunctionsFromObject(new HttpPlugin()).ToList();

            var fsReadOps    = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_file", "list_files", "grep_file", "get_file_summary", "get_file_info" };
            var shellReadOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "shell_run", "shell_get_env", "shell_which", "shell_get_working_directory" };
            var gitReadOps   = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "git_status", "git_diff", "git_log", "git_show", "git_branch_list", "git_stash_list" };
            var explorerTools = toolsByCategory["FileSystem"].Where(f => fsReadOps.Contains(f.Name))
                .Concat(toolsByCategory["Search"])
                .Concat(toolsByCategory["Shell"].Where(f => shellReadOps.Contains(f.Name)))
                .Concat(toolsByCategory["Git"].Where(f => gitReadOps.Contains(f.Name)))
                .ToList();
            subAgent = new SubAgentPlugin(factory.Create(modelConfig), explorerTools);

            (skillsPlugin, skillsCatalog) = ReplSkillsLoader.BuildSkills();
            if (skillsPlugin is not null)
                toolsByCategory["Skills"] = PluginRegistry.GetFunctionsFromObject(skillsPlugin).ToList();
        }

        var initialTools = toolsByCategory.Values.SelectMany(v => v).ToList();
        IChatClient client;
        try
        {
            client = ReplFactory.BuildClient(modelConfig, factory, initialTools.Count > 0);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not create chat client:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var cwd        = Directory.GetCurrentDirectory();
        var eventsPath = Path.Combine(cwd, FuseraftPaths.LocalReplEventsLog);

        // Load snapshot when --resume is specified.
        ReplSessionSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(settings.Resume))
        {
            snapshot = await ReplSessionSnapshot.LoadAsync(settings.Resume.Trim());
            if (snapshot is null)
            {
                AnsiConsole.MarkupLine($"[red]✗ No saved session found with ID '[/][bold]{Markup.Escape(settings.Resume.Trim())}[/][red]'.[/]");
                AnsiConsole.MarkupLine("[dim]  Use /sessions inside the REPL to list resumable sessions.[/]");
                return 1;
            }
        }

        var sessionId  = snapshot?.SessionId ?? GenerateSessionId();
        var startedAt  = snapshot?.StartedAt  ?? DateTime.UtcNow;

        if (!settings.NoTools)
            toolsByCategory["Session"] = PluginRegistry.GetFunctionsFromObject(
                new ReplSessionPlugin(sessionId, startedAt, modelId, cwd)).ToList();

        using var emitter = new EventEmitter(eventsPath);
        emitter.SetSessionId(sessionId);
        await emitter.EmitAsync("session_start", payload: new
        {
            model         = modelId,
            cwd,
            tools_enabled = !settings.NoTools,
            tool_count    = initialTools.Count,
            resumed       = snapshot is not null,
        });

        var memoryStore   = MemoryStore.ForRepl();
        var memoryEntries = await memoryStore.LoadAllAsync(cwd);
        var memoryBlock   = memoryEntries.Count > 0
            ? await memoryStore.BuildPromptBlockAsync(cwd)
            : null;
        var systemPrompt = BuildSystemPrompt(settings.SystemPrompt, initialTools.Count, cwd, memoryBlock, modelId, sessionId, startedAt);

        if (skillsCatalog is not null)
            systemPrompt += $"\n\n{skillsCatalog}";

        if (!jsonMode)
        {
            // Build plugin name list: tool categories + "Memory" if memories are loaded.
            var pluginNames = new List<string>(toolsByCategory.Keys);
            if (memoryBlock is not null) pluginNames.Add("Memory");

            MessageRenderer.RenderReplHeader(
                modelId, cwd, pluginNames, sessionId,
                memoryCount: memoryEntries.Count,
                skillCount:  skillsPlugin?.Count ?? 0,
                eventsPath:  settings.Verbose ? eventsPath : null);
        }

        var ctx = new ReplSessionContext(
            cwd, sessionId, startedAt, modelId, modelConfig, userCfg, client,
            factory, keyStore, emitter, eventsPath,
            memoryStore, toolsByCategory, systemPrompt, pendingSave,
            verbose: settings.Verbose, subAgent: subAgent)
        {
            JsonMode = jsonMode,
        };

        if (snapshot is not null)
        {
            var restored = snapshot.RestoreHistory();
            // Keep system prompt current (updated memories / AGENTS.md).
            if (restored.Count > 0 && restored[0].Role == ChatRole.System)
                restored[0] = new ChatMessage(ChatRole.System, systemPrompt);
            ctx.History.Clear();
            ctx.History.AddRange(restored);
            ctx.TurnIndex = snapshot.TurnIndex;

            if (!jsonMode)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]  Resuming session [bold]{Markup.Escape(sessionId)}[/] · {snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                    $"started {Markup.Escape(snapshot.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}[/]");
            }

            // Restore plan execution state so a crash mid-plan is transparent on resume.
            if (snapshot.ExecutionQueue is { Length: > 0 })
            {
                foreach (var e in snapshot.ExecutionQueue)
                    ctx.ExecutionQueue.Enqueue((e.Step, e.Total));
                if (!jsonMode)
                    AnsiConsole.MarkupLine(
                        $"[dim]  Plan in progress: {snapshot.ExecutionQueue.Length} step{(snapshot.ExecutionQueue.Length == 1 ? "" : "s")} queued — resuming automatically[/]");
            }
            else if (snapshot.PendingPlan is { Length: > 0 })
            {
                ctx.CurrentPlan = snapshot.PendingPlan;
                if (!jsonMode)
                    AnsiConsole.MarkupLine(
                        $"[dim]  Pending plan restored ({snapshot.PendingPlan.Length} step{(snapshot.PendingPlan.Length == 1 ? "" : "s")}). Run /execute to start.[/]");
            }
            if (snapshot.HaltedAt is not null)
            {
                ctx.HaltedAt = (snapshot.HaltedAt.Step, snapshot.HaltedAt.Total);
                if (snapshot.HaltedRemaining is { Length: > 0 })
                    foreach (var e in snapshot.HaltedRemaining)
                        ctx.HaltedRemaining.Enqueue((e.Step, e.Total));
                ctx.HaltedToolCalls = [.. snapshot.HaltedToolCalls ?? []];
                ctx.RecoveryHint    = snapshot.RecoveryHint;
                if (!jsonMode)
                    AnsiConsole.MarkupLine(
                        $"[yellow]  ⚠ Plan halted at step {snapshot.HaltedAt.Step.Step} of {snapshot.HaltedAt.Total}. Run /recover or /resume.[/]");
            }

            if (!jsonMode) AnsiConsole.WriteLine();
        }

        if (jsonMode)
            ReplJsonBridge.Emit(new { type = "ready", sessionId, model = modelId });

        await ReplTurn.RunAsync(ctx, cancellationToken);

        await emitter.EmitAsync("session_end", payload: new { turns = ctx.TurnIndex });
        await ReplTurn.ExtractMemoriesOnExitAsync(ctx);

        // Post-session skill curation (best-effort — never fails the session).
        if (userCfg?.SkillCuration?.Enabled == true)
            await RunSkillCurationAsync(ctx, userCfg.SkillCuration, loggerFactory, jsonMode);

        if (jsonMode)
            ReplJsonBridge.Emit(new { type = "session_end" });
        else
            AnsiConsole.MarkupLine("[dim]Session ended.[/]");
        return 0;
    }

    // -------------------------------------------------------------------------
    // Private setup helpers
    // -------------------------------------------------------------------------

    // Loads ShellPolicy from the default orchestration config in the working directory, if one exists.
    // Uses OrchestratorBuilder.LoadSecurityConfig which binds only Orchestration.Security and does
    // NOT run ResolveAgentFiles — a missing agent file therefore cannot silently drop the policy.
    private static ShellPolicy? TryLoadDefaultShellPolicy()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "config", "orchestration.yaml"),
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "config", "orchestration.json"),
        };

        foreach (var path in candidates)
        {
            try
            {
                var security = OrchestratorBuilder.LoadSecurityConfig(path);
                if (security?.ShellPolicy is { } policy)
                    return policy;
            }
            catch { /* best effort — malformed config should not crash the REPL */ }
        }

        return null;
    }

    private static string? ResolveModelId(ReplSettings settings, UserConfig? userCfg)
    {
        var modelId = settings.Model?.Trim();
        if (!string.IsNullOrEmpty(modelId)) return modelId;
        if (userCfg?.IsConfigured == true) return userCfg.ModelId;
        foreach (var (env, id) in AutoDetectOrder)
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env)))
                return id;
        return null;
    }

    private static string BuildSystemPrompt(
        string? settingsPrompt, int toolCount, string cwd, string? memoryBlock,
        string? modelId = null, string? sessionId = null, DateTime? startedAt = null)
    {
        string prompt;
        if (string.IsNullOrWhiteSpace(settingsPrompt))
        {
            var identity = modelId is not null
                ? $"You are the fuseraft assistant, running on {modelId}."
                : "You are the fuseraft assistant.";
            prompt = toolCount > 0
                ? $"{identity} You are a precise coding and research assistant with tools for files, shell, code search, git, and HTTP.\n" +
                  $"\nCurrent working directory: {cwd}\n" +
                  "\nGuidelines:\n" +
                  "- Prefer tools over guessing.\n" +
                  "- Read before writing or mutating.\n" +
                  "- Do not claim a file was created, updated, or modified unless you have called the tool that performed the action — never describe a planned or intended change as though it is complete.\n" +
                  "- Avoid destructive actions (rm, overwrite, force-push) unless explicitly requested.\n" +
                  "- Only write files the user explicitly requests — never create unsolicited summaries, changelogs, or status files.\n" +
                  "- For multi-step work, briefly state intent first.\n" +
                  "- If a command fails due to missing project/config file: search subdirs for the entry point, then run `cd <dir> && <command>` in one shell_run call. Note the directory used.\n" +
                  "- Always return to the original working directory for subsequent commands unless the task explicitly requires otherwise.\n"
                : $"{identity} The current working directory is: {cwd}.";
        }
        else
        {
            prompt = settingsPrompt + $"\n\nThe current working directory is: {cwd}.";
        }

        if (sessionId is not null)
        {
            var snapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fuseraft", "repl-sessions", $"repl-{sessionId}.json");
            var sessionStarted = startedAt.HasValue
                ? startedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                : "unknown";
            prompt +=
                $"\n\n# Current session\n" +
                $"Session ID: {sessionId}\n" +
                $"Started:    {sessionStarted}\n" +
                $"Snapshot:   {snapshotPath}\n" +
                $"Event log:  {Path.Combine(cwd, FuseraftPaths.LocalReplEventsLog)}\n" +
                $"Use the repl_session_* tools to inspect session metadata, list past sessions, or read log files.";
        }

        // Orient the REPL agent to the local .fuseraft/ folder so it never
        // wastes context scanning the directory to discover what is in it.
        // Logs are excluded here — the session block above already lists them
        // and directs the agent to use the repl_session_* tools for log access.
        if (toolCount > 0)
            prompt += $"\n\n{FuseraftPaths.BuildFolderOrientationBlock(includeLogs: false)}";

        var agentsBlock = ReadAgentsMd(cwd);
        if (agentsBlock is not null)
            prompt += $"\n\n{agentsBlock}";

        if (memoryBlock is not null)
            prompt += $"\n\n{memoryBlock}";

        return prompt;
    }

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

    private static string GenerateSessionId()
    {
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Runs the skill curator after a REPL session ends. Converts the chat history to the
    /// <see cref="AgentMessage"/> list the curator expects and fires a single LLM review call.
    /// Best-effort — any exception is swallowed so it never surfaces to the user as an error.
    /// </summary>
    private static async Task RunSkillCurationAsync(
        ReplSessionContext ctx,
        SkillCurationConfig curationConfig,
        ILoggerFactory loggerFactory,
        bool jsonMode)
    {
        try
        {
            await ctx.Emitter.EmitAsync("skill_curation_start",
                payload: new { session = ctx.SessionId, source = "repl" });

            // Convert ChatMessage history to AgentMessage list (assistant turns only).
            var messages = ctx.History
                .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                .Select((m, i) => new AgentMessage
                {
                    AgentName = "Assistant",
                    Content   = m.Text!,
                    Role      = "assistant",
                    TurnIndex = i,
                })
                .ToList();

            // Derive a task description from the first user message in the session.
            var taskDescription = ctx.History
                .FirstOrDefault(m => m.Role == ChatRole.User)?.Text?.Trim()
                ?? "REPL session";

            var checkpoint = new SessionCheckpoint
            {
                Task       = taskDescription,
                SessionId  = ctx.SessionId,
                ConfigPath = string.Empty,   // no YAML config in a REPL session
            };

            // Build a chat client for the curator (use configured model or fall back to session model).
            var curatorModelCfg = curationConfig.Model is { Length: > 0 } m
                ? ctx.Factory.Resolve(new ModelConfig { ModelId = m })
                : ctx.ModelConfig;
            using var curatorClient = ctx.Factory.Create(curatorModelCfg);

            var curator = new SkillCurator(
                curatorClient,
                curationConfig,
                evidenceStore: null,   // REPL has no EvidenceStore
                loggerFactory.CreateLogger<SkillCurator>());

            var result = await curator.RunAsync(checkpoint, messages, CancellationToken.None, source: "repl");

            await ctx.Emitter.EmitAsync("skill_curation_complete",
                payload: new
                {
                    session       = ctx.SessionId,
                    source        = "repl",
                    outcome       = result.Outcome.ToString().ToLowerInvariant(),
                    slug          = result.Slug,
                    path          = result.Path,
                    turns_digested = result.TurnsDigested,
                    failure_reason = result.FailureReason,
                });

            if (!jsonMode)
            {
                if (result.WroteSkill)
                    AnsiConsole.MarkupLine(
                        $"[green]✓ Skill {(result.Outcome == SkillCurationOutcome.Updated ? "updated" : "curated")}:[/] " +
                        $"[bold]{Markup.Escape(result.Slug!)}[/]  [dim]{Markup.Escape(result.Path!)}[/]");
                else if (result.Outcome == SkillCurationOutcome.Failed)
                    AnsiConsole.MarkupLine(
                        $"[dim yellow]Skill curation failed:[/] {Markup.Escape(result.FailureReason ?? "unknown error")}");
            }
        }
        catch (Exception ex)
        {
            // Curation is best-effort — log but never surface as an error.
            try
            {
                await ctx.Emitter.EmitAsync("skill_curation_complete",
                    payload: new { session = ctx.SessionId, source = "repl", outcome = "failed", failure_reason = ex.Message });
            }
            catch { /* emitter itself failed — nothing we can do */ }
        }
    }
}
