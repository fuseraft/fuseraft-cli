using System.ComponentModel;
using fuseraft.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fuseraft.Cli.Commands;

public sealed class InitSettings : CommandSettings
{
    [CommandArgument(0, "[output]")]
    [Description("Path to write the generated config (default: .fuseraft/config/orchestration.yaml).")]
    public string? OutputPath { get; set; }

    [CommandOption("-t|--template")]
    [Description("Team template: solo, pipeline, swe, brownfield, research, data, devops, debate, audit, magentic.")]
    public string? Template { get; set; }

    [CommandOption("-m|--model")]
    [Description("Model ID to use for all agents (auto-detected from API keys when omitted).")]
    public string? Model { get; set; }

    [CommandOption("-e|--endpoint")]
    [Description("Provider API endpoint URL (auto-detected from ~/.fuseraft/config when omitted).")]
    public string? Endpoint { get; set; }

    [CommandOption("--no-interactive")]
    [Description("Skip prompts and generate with the supplied options and defaults.")]
    public bool NoInteractive { get; set; }
}

/// <summary>
/// Generates a ready-to-run YAML orchestration config from a short interactive wizard
/// or from explicit flags for scripted / CI use.
/// </summary>
public sealed class InitCommand : AsyncCommand<InitSettings>
{
    private sealed record TemplateInfo(string Key, string Label, string Description);

    private static readonly TemplateInfo[] Templates =
    [
        new("solo",       "Solo Agent",
            "Single capable agent with investigation tooling and lossless compaction — the right starting point for simple tasks"),
        new("pipeline",   "Pipeline",
            "Planner → Developer → Tester → Reviewer as a directed graph with investigation tooling — no evidence contracts; use swe for production work"),
        new("swe",        "Software Engineering Team",
            "Planner → PlannerCritic → Developer → Tester → Reviewer — full safeguards: evidence contracts, hypothesis tracking, periodic Verifier, lossless compaction"),
        new("brownfield", "Brownfield Pipeline",
            "Archaeologist recons the codebase once → Planner → Developer → Reviewer as a graph; multi-target back-edges (REVISION REQUIRED → Developer, REPLAN REQUIRED → Planner)"),
        new("research",   "Research Team",
            "Researcher gathers cited findings → Critic adversarially reviews for gaps → Writer synthesises the final document"),
        new("data",       "Data Pipeline",
            "DataEngineer fetches and structures data → Analyst computes findings → Reporter synthesises a final document"),
        new("devops",     "DevOps Pipeline",
            "OpsPlanner writes an ops plan with rollback_command → Executor runs steps → Verifier health-checks; can trigger rollback"),
        new("debate",     "Debate Pipeline",
            "Proposer argues a position → Challenger critiques adversarially → Moderator synthesises a structured final verdict"),
        new("audit",      "Audit Pipeline",
            "Auditor scans for security / quality / compliance issues → Prioritizer triages by severity → Developer fixes → Verifier confirms"),
        new("magentic",   "Magentic Team",
            "AI-managed team: a manager LLM plans and coordinates 5 specialist workers dynamically; user approves the plan before execution"),
    ];

    private static readonly (string EnvVar, string Model)[] ProviderDefaults =
    [
        ("OPENAI_API_KEY",    "gpt-4o"),
        ("ANTHROPIC_API_KEY", "claude-sonnet-4-6"),
        ("XAI_API_KEY",       "grok-4.3"),
        ("GOOGLE_AI_API_KEY", "gemini-2.5-flash"),
        ("MISTRAL_API_KEY",   "mistral-medium-latest"),
        ("DEEPSEEK_API_KEY",  "deepseek-chat"),
    ];

    protected override async Task<int> ExecuteAsync(
        CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]fuseraft config generator[/]");
        AnsiConsole.MarkupLine("[dim]Generates a ready-to-run YAML orchestration config.[/]");
        AnsiConsole.WriteLine();

        var templateKey = ResolveTemplate(settings);
        if (templateKey is null) return 1;

        var model    = ResolveModel(settings);
        var endpoint = ResolveEndpoint(settings);
        var output   = ResolveOutputPath(settings);

        AnsiConsole.WriteLine();

        if (File.Exists(output))
        {
            if (settings.NoInteractive ||
                !AnsiConsole.Confirm($"[yellow]{Markup.Escape(output)} already exists. Overwrite?[/]"))
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return 1;
            }
        }

        var generated = InitTemplates.Build(templateKey, model, endpoint);
        var dir       = Path.GetDirectoryName(output) ?? string.Empty;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(output, generated.MainConfig, cancellationToken);

        var configDir = string.IsNullOrEmpty(dir) ? "." : dir;
        foreach (var (relativePath, content) in generated.AgentFiles)
        {
            var fullPath = Path.Combine(configDir, relativePath);
            var fileDir  = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(fileDir)) Directory.CreateDirectory(fileDir);
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }

        await EnsureGitignoreEntryAsync(cancellationToken);
        var knowledgeScaffold = await ScaffoldKnowledgeAsync(cancellationToken);

        var selected        = Array.Find(Templates, t => t.Key == templateKey)!;
        var endpointDisplay = string.IsNullOrWhiteSpace(endpoint) ? "[dim](default)[/]" : Markup.Escape(endpoint);
        AnsiConsole.MarkupLine($"[green]✓[/] Config written → [bold]{Markup.Escape(output)}[/]");
        foreach (var (relativePath, _) in generated.AgentFiles)
            AnsiConsole.MarkupLine($"  [green]↳[/] {Markup.Escape(Path.Combine(configDir, relativePath))}");
        foreach (var (path, created) in knowledgeScaffold)
        {
            var icon  = created ? "[green]✓[/]" : "[dim]·[/]";
            var label = created ? string.Empty  : " [dim](already exists)[/]";
            AnsiConsole.MarkupLine($"{icon} {Markup.Escape(path)}{label}");
        }
        AnsiConsole.MarkupLine($"[dim]Template:[/] {selected.Label}   [dim]Model:[/] {model}   [dim]Endpoint:[/] {endpointDisplay}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("").AddColumn("");
        table.AddRow("[dim]Review:[/]",   $"[dim]fuseraft config {Markup.Escape(output)}[/]");
        table.AddRow("[dim]Validate:[/]", $"[dim]fuseraft validate {Markup.Escape(output)}[/]");
        table.AddRow("[dim]Run:[/]",      $"[dim]fuseraft run --config {Markup.Escape(output)} \"Your task\"[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        return 0;
    }

    private string? ResolveTemplate(InitSettings settings)
    {
        var key = settings.Template?.Trim().ToLowerInvariant();
        if (key is null)
        {
            if (settings.NoInteractive)
            {
                key = "swe";
            }
            else
            {
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<TemplateInfo>()
                        .Title("Team type:")
                        .UseConverter(t => $"[bold]{t.Label}[/]  [dim]{t.Description}[/]")
                        .AddChoices(Templates));
                key = choice.Key;
            }
        }

        if (Array.Find(Templates, t => t.Key == key) is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Unknown template:[/] '{key}'. " +
                $"Valid: {string.Join(", ", Templates.Select(t => t.Key))}");
            return null;
        }

        return key;
    }

    private static string ResolveModel(InitSettings settings)
    {
        var defaultModel = settings.Model ?? DetectDefaultModel();
        if (settings.NoInteractive || settings.Model is not null) return defaultModel;

        var model = AnsiConsole.Prompt(
            new TextPrompt<string>($"Model ID [dim](detected: {defaultModel})[/]:")
                .DefaultValue(defaultModel)
                .AllowEmpty());
        return string.IsNullOrWhiteSpace(model) ? defaultModel : model;
    }

    private static string? ResolveEndpoint(InitSettings settings)
    {
        var configEndpoint  = UserConfigStore.Load().Config?.Endpoint;
        var defaultEndpoint = settings.Endpoint
            ?? (string.IsNullOrWhiteSpace(configEndpoint) ? null : configEndpoint);

        if (settings.NoInteractive || settings.Endpoint is not null) return defaultEndpoint;

        var prompt = new TextPrompt<string>("Provider URL:").AllowEmpty();
        if (defaultEndpoint is not null) prompt.DefaultValue(defaultEndpoint);
        var input = AnsiConsole.Prompt(prompt);
        return string.IsNullOrWhiteSpace(input) ? defaultEndpoint : input;
    }

    private static string ResolveOutputPath(InitSettings settings)
    {
        var defaultPath = settings.OutputPath ?? ".fuseraft/config/orchestration.yaml";
        if (settings.NoInteractive || settings.OutputPath is not null) return defaultPath;

        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("Output path:")
                .DefaultValue(defaultPath)
                .AllowEmpty());
        return string.IsNullOrWhiteSpace(path) ? defaultPath : path;
    }

    private static async Task<IReadOnlyList<(string Path, bool Created)>> ScaffoldKnowledgeAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<(string, bool)>();

        // Directories — always created (idempotent).
        var dirs = new[]
        {
            ".fuseraft/knowledge/decisions/archive",
            ".fuseraft/knowledge/repository",
            ".fuseraft/knowledge/objectives",
        };
        foreach (var d in dirs)
            Directory.CreateDirectory(d);

        // architecture.yaml — only if absent.
        const string archPath = ".fuseraft/architecture.yaml";
        if (!File.Exists(archPath))
        {
            await File.WriteAllTextAsync(archPath, DefaultArchitectureYaml, cancellationToken);
            result.Add((archPath, true));
        }
        else
        {
            result.Add((archPath, false));
        }

        // lifecycle.yaml — only if absent.
        const string lcPath = ".fuseraft/knowledge/lifecycle.yaml";
        if (!File.Exists(lcPath))
        {
            await File.WriteAllTextAsync(lcPath, DefaultLifecycleYaml, cancellationToken);
            result.Add((lcPath, true));
        }
        else
        {
            result.Add((lcPath, false));
        }

        // .fuseraftignore — only if absent.
        const string ignorePath = ".fuseraft/.fuseraftignore";
        if (!File.Exists(ignorePath))
        {
            await File.WriteAllTextAsync(ignorePath, DefaultFuseraftIgnore, cancellationToken);
            result.Add((ignorePath, true));
        }
        else
        {
            result.Add((ignorePath, false));
        }

        return result;
    }

    private const string DefaultArchitectureYaml = """
        # Architecture layer manifest — fuseraft arch check reads this file.
        #
        # Language: which source files and import statements to scan.
        # Supported values:
        #   csharp (default)  python  java  typescript  javascript  go  rust  ruby
        # Unknown values fall back to csharp.
        #
        Language: csharp

        # Layers define named regions of your codebase and their allowed dependencies.
        #
        # Name        — display name used in violation reports.
        # Paths       — source path prefixes that belong to this layer (relative to project root).
        # Namespaces  — module/namespace prefixes owned by this layer.
        #               csharp: inferred as "fuseraft.<Name>" when omitted.
        #               All other languages: must be declared explicitly. Examples:
        #                 python     — myapp.core
        #                 java       — com.example.core
        #                 typescript — src/core  (or @myorg/core for packages)
        #                 go         — github.com/myorg/myrepo/core
        #                 rust       — myapp::core
        #                 ruby       — myapp/core
        # MayDependOn — names of layers this layer is allowed to import from.
        #               Omit or leave empty to forbid all cross-layer imports.
        #
        # Quick start — run `fuseraft repl` and paste this prompt to auto-populate:
        #   "Read the source tree and populate .fuseraft/architecture.yaml with the
        #    actual layers, source paths, namespace prefixes, and MayDependOn rules
        #    for this project. Set Language to the project's primary language.
        #    Use write_file to save the result."
        #
        Layers:
          - Name: Core
            Paths:
              - src/Core/
            MayDependOn: []

          - Name: Infrastructure
            Paths:
              - src/Infrastructure/
            MayDependOn:
              - Core

          - Name: Orchestration
            Paths:
              - src/Orchestration/
            MayDependOn:
              - Core
              - Infrastructure

          - Name: Cli
            Paths:
              - src/Cli/
            MayDependOn:
              - Core
              - Infrastructure
              - Orchestration
        """;

    private const string DefaultLifecycleYaml = """
        # Knowledge lifecycle policy — fuseraft knowledge gc reads this file.
        # All values are in days. Run: fuseraft knowledge gc
        #
        # AdrRetentionDays: days after Superseded status before archiving (0 = immediate).
        AdrRetentionDays: 0
        #
        # MemoryReinforceWindowDays: Approved memories not reinforced within this window
        #   are demoted back to Candidate for re-review.
        MemoryReinforceWindowDays: 90
        #
        # ConfidenceDecayDays: Verified provenance claims older than this (with no ExpiresAt)
        #   decay to Inferred. Set to 0 to disable decay.
        ConfidenceDecayDays: 30
        #
        # OrphanedNodeGracePeriodDays: graph nodes with no edges and no recent file touch
        #   are pruned after this many days. Set to 0 to disable.
        OrphanedNodeGracePeriodDays: 7
        #
        # MaxProvenanceAgeDays: expired provenance records (past ExpiresAt) are archived
        #   after this many additional days. 0 = archive immediately.
        MaxProvenanceAgeDays: 0
        #
        # MemoryCandidatePruningDays: Candidate memories not reinforced within this window
        #   are permanently deleted from knowledge/repository/. Set to 0 to disable.
        MemoryCandidatePruningDays: 180
        """;

    private const string DefaultFuseraftIgnore = """
        # .fuseraftignore — marks which .fuseraft/ files fuseraft tooling treats as ephemeral.
        # Paths are relative to .fuseraft/. Syntax is gitignore-style; prefix ! to un-ignore.
        #
        # Respected by: fuseraft cleanup, fuseraft gc, fuseraft archive-session
        # Does not affect .gitignore — git tracking is controlled by your project's .gitignore.

        # ── Ephemeral session data ──────────────────────────────────────────────────
        # Large, agent-internal files that are reproducible and not useful to retain.
        sessions/**/read_cache.json
        sessions/**/tool-results/
        sessions/**/ctx_viz.html
        sessions/**/events.jsonl
        sessions/**/brief-review.json

        # ── Logs ───────────────────────────────────────────────────────────────────
        logs/**

        # ── State ──────────────────────────────────────────────────────────────────
        state/knowledge_findings.json
        state/provenance.archive.json

        # ── Keep these ─────────────────────────────────────────────────────────────
        # Session artifacts worth retaining for inspection and handoff continuity.
        !sessions/*/brief.json
        !sessions/*/brief.brownfield.json
        !sessions/*/conventions.json
        !sessions/*/context_summary.md
        !sessions/*/intents.json
        """;

    private static async Task EnsureGitignoreEntryAsync(CancellationToken cancellationToken)
    {
        var gitignorePath = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        if (!File.Exists(gitignorePath)) return;

        var lines = await File.ReadAllLinesAsync(gitignorePath, cancellationToken);

        // Remove old blanket entry — entire .fuseraft/ should now be tracked.
        var blanketIndex = Array.FindIndex(lines, l => l.Trim() == ".fuseraft");
        if (blanketIndex >= 0)
        {
            var updated = lines.ToList();
            updated.RemoveAt(blanketIndex);
            await File.WriteAllLinesAsync(gitignorePath, updated, cancellationToken);
            lines = [.. updated];
        }

        // Remove old allowlist block — runtime artifacts are now global, not local.
        if (lines.Any(l => l.Trim() == ".fuseraft/*"))
        {
            var updated = lines
                .Where(l =>
                {
                    var t = l.Trim();
                    return t != ".fuseraft/*"
                        && t != "!.fuseraft/.fuseraftignore"
                        && t != "!.fuseraft/config/"  && t != "!.fuseraft/config/**"
                        && t != "!.fuseraft/context/" && t != "!.fuseraft/context/**"
                        && t != "!.fuseraft/knowledge/" && t != "!.fuseraft/knowledge/**"
                        && t != ".fuseraft/knowledge/repository/";
                })
                .ToList();
            // Also strip the comment line that typically precedes the block.
            updated = updated
                .Where(l => !l.TrimStart('#', ' ').StartsWith("fuseraft runtime artifact", StringComparison.OrdinalIgnoreCase))
                .ToList();
            await File.WriteAllLinesAsync(gitignorePath, updated, cancellationToken);
            lines = [.. updated];
        }

        // Already has the new denylist block — nothing to do.
        if (lines.Any(l => l.Contains(".fuseraft/state/"))) return;

        const string block = """

            # .fuseraft/ — user-authored; runtime artifacts live globally in ~/.fuseraft/
            # Stale local runtime dirs from before the global migration — delete once confirmed empty
            .fuseraft/state/
            .fuseraft/logs/
            .fuseraft/sessions/
            .fuseraft/knowledge/repository/
            .fuseraft/memory/
            """;

        await File.AppendAllTextAsync(gitignorePath, block + Environment.NewLine, cancellationToken);
        AnsiConsole.MarkupLine("[green]✓[/] Updated [bold].gitignore[/] — [dim].fuseraft/[/] user-authored content will be tracked; stale runtime dirs excluded");
    }

    private static string DetectDefaultModel()
    {
        var saved = UserConfigStore.Load().Config?.ModelId;
        if (!string.IsNullOrWhiteSpace(saved)) return saved;

        foreach (var (envVar, model) in ProviderDefaults)
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
                return model;
        return "gpt-4o";
    }
}
