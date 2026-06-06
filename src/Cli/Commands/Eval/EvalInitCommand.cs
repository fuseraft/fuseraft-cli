using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fuseraft.Cli.Commands.Eval;

public sealed class EvalInitSettings : CommandSettings
{
    [CommandArgument(0, "[output]")]
    [Description("Path to write the generated suite (default: .fuseraft/evals/suite.yaml).")]
    public string? OutputPath { get; set; }

    [CommandOption("-n|--name")]
    [Description("Name of the eval suite.")]
    public string? Name { get; set; }

    [CommandOption("-c|--config")]
    [Description("Default team config path to embed in the suite.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--no-interactive")]
    [Description("Skip prompts and write a suite with the supplied options and defaults.")]
    public bool NoInteractive { get; set; }
}

/// <summary>
/// Scaffolds a new eval suite YAML file with annotated example cases.
/// </summary>
public sealed class EvalInitCommand : AsyncCommand<EvalInitSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, EvalInitSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]fuseraft eval init[/]");
        AnsiConsole.MarkupLine("[dim]Scaffolds a new eval suite YAML.[/]");
        AnsiConsole.WriteLine();

        var output     = ResolveOutputPath(settings);
        var suiteName  = ResolveName(settings, output);
        var configPath = ResolveConfigPath(settings);

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

        var dir = Path.GetDirectoryName(output) ?? string.Empty;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var content = BuildSuite(suiteName, configPath);
        await File.WriteAllTextAsync(output, content, cancellationToken);

        AnsiConsole.MarkupLine($"[green]✓[/] Eval suite written → [bold]{Markup.Escape(output)}[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("").AddColumn("");
        table.AddRow("[dim]Edit:[/]",     $"[dim]{Markup.Escape(output)}[/]");
        table.AddRow("[dim]Run:[/]",      $"[dim]fuseraft eval run {Markup.Escape(output)}[/]");
        table.AddRow("[dim]Filter:[/]",   $"[dim]fuseraft eval run {Markup.Escape(output)} --filter smoke[/]");
        table.AddRow("[dim]CI mode:[/]",  $"[dim]fuseraft eval run {Markup.Escape(output)} --ci[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        return 0;
    }

    private static string ResolveOutputPath(EvalInitSettings settings)
    {
        if (settings.OutputPath is not null)
            return Path.GetFullPath(settings.OutputPath);

        if (settings.NoInteractive)
            return Path.GetFullPath(".fuseraft/evals/suite.yaml");

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("Output path:")
                .DefaultValue(".fuseraft/evals/suite.yaml")
                .AllowEmpty());
        return Path.GetFullPath(string.IsNullOrWhiteSpace(input) ? ".fuseraft/evals/suite.yaml" : input);
    }

    private static string ResolveName(EvalInitSettings settings, string outputPath)
    {
        if (settings.Name is not null) return settings.Name;

        var defaultName = Path.GetFileNameWithoutExtension(outputPath)
            .Replace('-', ' ').Replace('_', ' ');
        defaultName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(defaultName);

        if (settings.NoInteractive) return defaultName;

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("Suite name:")
                .DefaultValue(defaultName)
                .AllowEmpty());
        return string.IsNullOrWhiteSpace(input) ? defaultName : input;
    }

    private static string ResolveConfigPath(EvalInitSettings settings)
    {
        const string defaultConfig = ".fuseraft/config/orchestration.yaml";

        if (settings.ConfigPath is not null) return settings.ConfigPath;
        if (settings.NoInteractive) return defaultConfig;

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("Default team config path:")
                .DefaultValue(defaultConfig)
                .AllowEmpty());
        return string.IsNullOrWhiteSpace(input) ? defaultConfig : input;
    }

    private static string BuildSuite(string name, string configPath) => $"""
        name: {name}
        # Suite-level default config. Override per-case with the 'config' key.
        config: {configPath}

        cases:
          # Smoke test — quick sanity check that the team responds at all.
          - id: smoke-basic
            task: "Say hello and confirm you are ready."
            must_succeed: true
            expect_keywords:
              - hello
            max_turns: 3
            tags:
              - smoke

          # Keyword check — verify the output contains required content.
          - id: code-generation
            task: "Write a Python function named reverse_string that returns the reverse of its input."
            must_succeed: true
            expect_keywords:
              - def reverse_string
              - return
            expect_regex:
              - "def reverse_string\\("
            max_turns: 5
            tags:
              - coding

          # Forbidden-keyword check — guard against undesirable response patterns.
          - id: no-refusal
            task: "List three benefits of automated testing."
            must_succeed: true
            forbidden_keywords:
              - "I cannot"
              - "I'm unable"
              - "I am unable"
            tags:
              - quality

          # Task from file — useful for long or multi-line prompts.
          # Create the file at the path below before running this case.
          # - id: file-task
          #   task_file: .fuseraft/evals/tasks/my-task.txt
          #   must_succeed: true
          #   max_turns: 10
          #   tags:
          #     - file-task

          # Per-case config override — run this case against a different team.
          # - id: specialist-check
          #   config: .fuseraft/config/specialist.yaml
          #   task: "Explain the role of a load balancer in two sentences."
          #   must_succeed: true
          #   expect_keywords:
          #     - load balancer
          #   tags:
          #     - routing
        """;
}
