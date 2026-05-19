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
    [Description("Team template: dev-team, research, devops, content, minimal, magentic, brownfield, designer, graph, brownfield-graph.")]
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
        new("dev-team",  "Software Development Team",
            "Planner → Developer → Tester → Reviewer with state machine routing, evidence contracts, and self-verification"),
        new("research",  "Research Team",
            "Researcher → Writer with state machine routing and evidence-gated handoff"),
        new("devops",    "DevOps Team",
            "Planner → Developer → Operator with state machine routing and shell tooling"),
        new("content",   "Content Pipeline",
            "Writer → Editor with state machine routing and draft verification"),
        new("minimal",       "Minimal — Single Agent",
            "One general-purpose agent for simple tasks"),
        new("magentic",      "Magentic Team",
            "AI-managed team: a manager LLM plans and coordinates participants dynamically"),
        new("brownfield",    "Brownfield Codebase Pipeline",
            "Archaeologist recons the codebase → Planner → Developer (change-envelope enforced) → Reviewer"),
        new("designer",      "Orchestration Designer",
            "A single agent that helps you design, write, and validate fuseraft orchestration configs"),
        new("graph",            "Graph Pipeline",
            "Planner → Developer → Tester → Reviewer as a declarative directed graph with keyword-routed forward and back-edges"),
        new("brownfield-graph", "Brownfield Graph Pipeline",
            "Archaeologist → Planner → Developer → Reviewer as a directed graph; Reviewer routes to Developer OR Planner on failure — showcasing multi-target back-edges"),
    ];

    private static readonly (string EnvVar, string Model)[] ProviderDefaults =
    [
        ("OPENAI_API_KEY",    "gpt-4o"),
        ("ANTHROPIC_API_KEY", "claude-sonnet-4-6"),
        ("XAI_API_KEY",       "grok-4"),
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

        var selected        = Array.Find(Templates, t => t.Key == templateKey)!;
        var endpointDisplay = string.IsNullOrWhiteSpace(endpoint) ? "[dim](default)[/]" : Markup.Escape(endpoint);
        AnsiConsole.MarkupLine($"[green]✓[/] Config written → [bold]{Markup.Escape(output)}[/]");
        foreach (var (relativePath, _) in generated.AgentFiles)
            AnsiConsole.MarkupLine($"  [green]↳[/] {Markup.Escape(Path.Combine(configDir, relativePath))}");
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
                key = "dev-team";
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

    private static async Task EnsureGitignoreEntryAsync(CancellationToken cancellationToken)
    {
        var gitignorePath = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        if (!File.Exists(gitignorePath)) return;

        var lines = await File.ReadAllLinesAsync(gitignorePath, cancellationToken);

        // If the old blanket entry exists, replace it with the selective block.
        var blanketIndex = Array.FindIndex(lines, l => l.Trim() == ".fuseraft");
        if (blanketIndex >= 0)
        {
            var updated = lines.ToList();
            updated.RemoveAt(blanketIndex);
            await File.WriteAllLinesAsync(gitignorePath, updated, cancellationToken);
            lines = [.. updated];
        }

        // Already has the selective block — nothing to do.
        if (lines.Any(l => l.Trim() == ".fuseraft/*")) return;

        const string block = """

            # fuseraft runtime artifacts — config/ and context/ remain tracked
            .fuseraft/*
            !.fuseraft/config/
            !.fuseraft/config/**
            !.fuseraft/context/
            !.fuseraft/context/**
            """;

        await File.AppendAllTextAsync(gitignorePath, block + Environment.NewLine, cancellationToken);
        AnsiConsole.MarkupLine("[green]✓[/] Updated [bold].gitignore[/] — [dim].fuseraft/config/[/] and [dim].fuseraft/context/[/] will be tracked");
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
