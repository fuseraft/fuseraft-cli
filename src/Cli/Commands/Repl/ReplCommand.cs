using System.ComponentModel;
using Microsoft.Extensions.AI;
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
}

public sealed class ReplCommand : AsyncCommand<ReplSettings>
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
        if (!settings.NoBanner)
            MessageRenderer.RenderBanner();

        var keyStore = ApiKeyStoreFactory.Create();
        var (userCfg, legacyKey) = UserConfigStore.Load();

        if (OrchestratorBuilder.VsCodeMode)
        {
            // Running from VS Code: API key is in the env var the extension injected.
            if (userCfg is not null)
                userCfg.ApiKey = Environment.GetEnvironmentVariable("FUSERAFT_API_KEY") ?? string.Empty;
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
            AnsiConsole.MarkupLine("[red]✗ No model specified and no supported API key found.[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]fuseraft repl[/] [dim]to configure, or pass[/] [bold]--model[/].");
            return 1;
        }

        var modelConfig = ReplFactory.BuildModelConfig(modelId, userCfg);
        using var factory = new ChatClientFactory();

        var toolsByCategory = new Dictionary<string, List<AIFunction>>(StringComparer.OrdinalIgnoreCase);
        using ShellPlugin? shellPlugin = settings.NoTools ? null : new ShellPlugin();
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

        AnsiConsole.Write(new Rule($"[bold cyan]{Markup.Escape(modelId)}[/]")
            .LeftJustified()
            .RuleStyle(new Spectre.Console.Style(Spectre.Console.Color.Grey)));
        AnsiConsole.WriteLine();

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

        var memoryStore  = MemoryStore.ForRepl();
        var memoryBlock  = await memoryStore.BuildPromptBlockAsync(cwd);
        var systemPrompt = BuildSystemPrompt(settings.SystemPrompt, initialTools.Count, cwd, memoryBlock, modelId);

        if (skillsCatalog is not null)
            systemPrompt += $"\n\n{skillsCatalog}";

        // Single compact info line.
        var infoParts = new List<string>();
        if (toolsByCategory.Count > 0)
            infoParts.Add(string.Join("  ", toolsByCategory.Keys));
        if (File.Exists(Path.Combine(cwd, "AGENTS.md"))) infoParts.Add("agents");
        if (memoryBlock is not null)                      infoParts.Add("memory");
        if (skillsPlugin is not null)                     infoParts.Add($"{skillsPlugin.Count} skill{(skillsPlugin.Count == 1 ? "" : "s")}");
        if (subAgent is not null)                         infoParts.Add("/explore  /locate  /adversarial");
        infoParts.Add("/help");
        AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(string.Join("  ·  ", infoParts))}[/]");
        AnsiConsole.MarkupLine($"[dim]  session: {Markup.Escape(sessionId)}[/]");
        if (settings.Verbose)
            AnsiConsole.MarkupLine($"[dim]  events: {Markup.Escape(eventsPath)}[/]");
        AnsiConsole.WriteLine();

        var ctx = new ReplSessionContext(
            cwd, sessionId, startedAt, modelId, modelConfig, userCfg, client,
            factory, keyStore, emitter, eventsPath,
            memoryStore, toolsByCategory, systemPrompt, pendingSave,
            verbose: settings.Verbose, subAgent: subAgent);

        if (snapshot is not null)
        {
            var restored = snapshot.RestoreHistory();
            // Keep system prompt current (updated memories / AGENTS.md).
            if (restored.Count > 0 && restored[0].Role == ChatRole.System)
                restored[0] = new ChatMessage(ChatRole.System, systemPrompt);
            ctx.History.Clear();
            ctx.History.AddRange(restored);
            ctx.TurnIndex = snapshot.TurnIndex;
            AnsiConsole.MarkupLine(
                $"[dim]  Resuming session [bold]{Markup.Escape(sessionId)}[/] · {snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {Markup.Escape(snapshot.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}[/]");

            // Restore plan execution state so a crash mid-plan is transparent on resume.
            if (snapshot.ExecutionQueue is { Length: > 0 })
            {
                foreach (var e in snapshot.ExecutionQueue)
                    ctx.ExecutionQueue.Enqueue((e.Step, e.Total));
                AnsiConsole.MarkupLine(
                    $"[dim]  Plan in progress: {snapshot.ExecutionQueue.Length} step{(snapshot.ExecutionQueue.Length == 1 ? "" : "s")} queued — resuming automatically[/]");
            }
            else if (snapshot.PendingPlan is { Length: > 0 })
            {
                ctx.CurrentPlan = snapshot.PendingPlan;
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
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ Plan halted at step {snapshot.HaltedAt.Step.Step} of {snapshot.HaltedAt.Total}. Run /recover or /resume.[/]");
            }

            AnsiConsole.WriteLine();
        }

        await ReplTurn.RunAsync(ctx, cancellationToken);

        await emitter.EmitAsync("session_end", payload: new { turns = ctx.TurnIndex });
        await ReplTurn.ExtractMemoriesOnExitAsync(ctx);

        AnsiConsole.MarkupLine("[dim]Session ended.[/]");
        return 0;
    }

    // -------------------------------------------------------------------------
    // Private setup helpers
    // -------------------------------------------------------------------------

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
        string? settingsPrompt, int toolCount, string cwd, string? memoryBlock, string? modelId = null)
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
}
