using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Eval;

public sealed class EvalSettings : CommandSettings
{
    [CommandArgument(0, "[suite]")]
    [Description("Path to the eval suite YAML or JSON file (default: .fuseraft/evals/suite.yaml).")]
    public string? Suite { get; set; }

    [CommandOption("-c|--config")]
    [Description("Override the suite-level team config path.")]
    public string? ConfigPath { get; set; }

    [CommandOption("-o|--output")]
    [Description("Write per-case results as JSONL to this file.")]
    public string? OutputPath { get; set; }

    [CommandOption("--filter")]
    [Description("Run only cases whose id or tag contains this value (case-insensitive substring).")]
    public string? Filter { get; set; }

    [CommandOption("--no-banner")]
    [Description("Skip the suite header.")]
    public bool NoBanner { get; set; }

    [CommandOption("--ci")]
    [Description("Exit 1 if any case fails (for CI pipelines).")]
    public bool Ci { get; set; }
}

/// <summary>
/// Runs an eval suite against a team config and reports pass/fail per case.
/// Usage: fuseraft eval [suite.yaml] [--config team.yaml] [--filter tag] [--output results.jsonl]
/// </summary>
public sealed class EvalCommand(ILoggerFactory loggerFactory, PluginRegistry pluginRegistry)
    : AsyncCommand<EvalSettings>
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    protected override async Task<int> ExecuteAsync(CommandContext context, EvalSettings settings, CancellationToken cancellationToken)
    {
        var suitePath = Path.GetFullPath(settings.Suite ?? ".fuseraft/evals/suite.yaml");

        if (!File.Exists(suitePath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Suite file not found:[/] {Markup.Escape(suitePath)}");
            return 1;
        }

        EvalSuite suite;
        try
        {
            suite = LoadSuite(suitePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Failed to load suite:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (suite.Cases.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Suite has no cases.[/]");
            return 0;
        }

        var cases = ApplyFilter(suite.Cases, settings.Filter);
        if (cases.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ No cases match filter '{Markup.Escape(settings.Filter ?? "")}'.[/]");
            return 0;
        }

        if (!settings.NoBanner)
        {
            AnsiConsole.MarkupLine($"[bold]Eval suite:[/] {Markup.Escape(suite.Name)}  [dim]{cases.Count} case(s)[/]");
            AnsiConsole.WriteLine();
        }

        var results         = new List<EvalCaseResult>();
        var approvalService = new ConsoleHumanApprovalService();

        foreach (var evalCase in cases)
        {
            var configPath = Path.GetFullPath(
                evalCase.Config
                ?? settings.ConfigPath
                ?? suite.Config
                ?? ".fuseraft/config/orchestration.yaml");

            if (!File.Exists(configPath))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  config not found: {Markup.Escape(configPath)}");
                results.Add(Failed(evalCase.Id, "—", $"config not found: {configPath}"));
                continue;
            }

            string? task = null;
            if (evalCase.TaskFile is not null)
            {
                var absFile = Path.IsPathRooted(evalCase.TaskFile)
                    ? evalCase.TaskFile
                    : Path.GetFullPath(evalCase.TaskFile);
                if (!File.Exists(absFile))
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  task_file not found: {Markup.Escape(absFile)}");
                    results.Add(Failed(evalCase.Id, "—", $"task_file not found: {absFile}"));
                    continue;
                }
                task = (await File.ReadAllTextAsync(absFile, cancellationToken)).Trim();
            }
            else
            {
                task = evalCase.Task?.Trim();
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  no task defined");
                results.Add(Failed(evalCase.Id, "—", "no task defined for this case"));
                continue;
            }

            var sessionId = Guid.NewGuid().ToString("N")[..8];
            AnsiConsole.Markup($"  {Markup.Escape(evalCase.Id.PadRight(42))}");

            SessionResult sessionResult;
            try
            {
                var built = await OrchestratorBuilder.BuildAsync(
                    configPath, loggerFactory, pluginRegistry, approvalService,
                    hitlMode: false, sessionId: sessionId);

                var (orchestrator, config, mcpManager, compactor, changeTracker, eventEmitter,
                     governanceKernel, skillCurator, repoMemoryExtractor, _, sessionMetrics) = built;

                await using var _mcp = mcpManager;
                using  var _gov      = governanceKernel;

                await OrchestratorBuilder.ValidateApiKeysAsync(config);

                var evalStore  = new InMemorySessionStore();
                var checkpoint = new SessionCheckpoint
                {
                    SessionId        = sessionId,
                    Task             = task,
                    ConfigPath       = configPath,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };
                await evalStore.SaveAsync(checkpoint, cancellationToken);

                eventEmitter?.SetSessionId(sessionId);
                orchestrator.SetSessionId(sessionId);
                compactor?.SetSessionId(sessionId);
                orchestrator.SetStructuredTask(TaskModel.FromGoal(task));

                var runner = new SessionRunner(
                    orchestrator, compactor, evalStore, approvalService,
                    eventEmitter:   null,
                    telemetry:      null,
                    modelIdByAgent: config.Agents.ToDictionary(
                        a => a.Name,
                        a => string.IsNullOrWhiteSpace(a.Model.ModelId) ? "unknown" : a.Model.ModelId,
                        StringComparer.OrdinalIgnoreCase),
                    devUI:          null,
                    configPath:     configPath,
                    maxIterations:  config.Termination?.ResolveMaxIterations() ?? 0,
                    contextBudget:  config.ContextBudget,
                    sessionMetrics: sessionMetrics);

                sessionResult = await runner.RunAsync(task, checkpoint, hitlMode: false, showTools: false, cancellationToken);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]");
                results.Add(Failed(evalCase.Id, sessionId, $"orchestrator exception: {ex.Message}", ex.Message));
                continue;
            }

            var caseResult = Score(evalCase, sessionResult, sessionId);
            results.Add(caseResult);
            PrintCaseResult(caseResult);
        }

        AnsiConsole.WriteLine();
        PrintSummary(results);

        if (settings.OutputPath is not null)
            await WriteJsonlAsync(results, settings.OutputPath);

        return settings.Ci && results.Any(r => !r.Passed) ? 1 : 0;
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    private static EvalCaseResult Score(EvalCase evalCase, SessionResult result, string sessionId)
    {
        var failures = new List<string>();

        if (evalCase.MustSucceed && !result.Succeeded)
            failures.Add($"session did not succeed: {result.ErrorMessage ?? "unknown"}");

        var finalContent = result.Messages
            .LastOrDefault(m => m.Role == "assistant")?.Content ?? string.Empty;

        foreach (var kw in evalCase.ExpectKeywords)
            if (!finalContent.Contains(kw, StringComparison.OrdinalIgnoreCase))
                failures.Add($"expected keyword not found: \"{kw}\"");

        foreach (var pattern in evalCase.ExpectRegex)
        {
            try
            {
                if (!Regex.IsMatch(finalContent, pattern, RegexOptions.IgnoreCase))
                    failures.Add($"regex not matched: {pattern}");
            }
            catch (ArgumentException)
            {
                failures.Add($"invalid regex pattern: {pattern}");
            }
        }

        foreach (var kw in evalCase.ForbiddenKeywords)
            if (finalContent.Contains(kw, StringComparison.OrdinalIgnoreCase))
                failures.Add($"forbidden keyword found: \"{kw}\"");

        if (evalCase.MaxTurns > 0 && result.Messages.Count > evalCase.MaxTurns)
            failures.Add($"exceeded max_turns: {result.Messages.Count} > {evalCase.MaxTurns}");

        return new EvalCaseResult
        {
            CaseId            = evalCase.Id,
            SessionId         = sessionId,
            Passed            = failures.Count == 0,
            FailureReasons    = failures,
            TotalTurns        = result.Messages.Count,
            DurationMs        = (long)result.Elapsed.TotalMilliseconds,
            TotalInputTokens  = result.Messages.Sum(m => (long)(m.Usage?.InputTokens  ?? 0)),
            TotalOutputTokens = result.Messages.Sum(m => (long)(m.Usage?.OutputTokens ?? 0)),
            ErrorMessage      = result.ErrorMessage,
        };
    }

    // ── Display ───────────────────────────────────────────────────────────────

    private static void PrintCaseResult(EvalCaseResult r)
    {
        var icon   = r.Passed ? "[green]PASS[/]" : "[red]FAIL[/]";
        var tokens = r.TotalInputTokens > 0
            ? $"  [dim]in:{r.TotalInputTokens:N0} out:{r.TotalOutputTokens:N0}[/]"
            : string.Empty;

        AnsiConsole.MarkupLine($"{icon}  [dim]{r.TotalTurns} turn(s)  {r.DurationMs:N0}ms{tokens}[/]");

        foreach (var reason in r.FailureReasons)
            AnsiConsole.MarkupLine($"    [red]→[/] {Markup.Escape(reason)}");
    }

    private static void PrintSummary(List<EvalCaseResult> results)
    {
        var passed = results.Count(r => r.Passed);
        var total  = results.Count;
        var color  = passed == total ? "green" : passed == 0 ? "red" : "yellow";

        AnsiConsole.MarkupLine(
            $"[{color}]{passed}/{total} passed[/]" +
            (passed < total ? $"  [red]{total - passed} failed[/]" : string.Empty));
    }

    // ── I/O ───────────────────────────────────────────────────────────────────

    private static async Task WriteJsonlAsync(List<EvalCaseResult> results, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var writer = new StreamWriter(path, append: false);
            foreach (var r in results)
                await writer.WriteLineAsync(JsonSerializer.Serialize(r, JsonWriteOpts));

            AnsiConsole.MarkupLine($"[dim]Results → {Markup.Escape(path)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not write results: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static EvalSuite LoadSuite(string path)
    {
        var ext     = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);

        if (ext is ".yaml" or ".yml")
            return YamlDeserializer.Deserialize<EvalSuite>(content)
                   ?? throw new InvalidDataException("Suite file is empty.");

        return JsonSerializer.Deserialize<EvalSuite>(content, JsonOpts)
               ?? throw new InvalidDataException("Suite file is empty.");
    }

    private static List<EvalCase> ApplyFilter(List<EvalCase> cases, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return cases;
        return cases
            .Where(c =>
                c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static EvalCaseResult Failed(string caseId, string sessionId, string reason, string? error = null) =>
        new()
        {
            CaseId         = caseId,
            SessionId      = sessionId,
            Passed         = false,
            FailureReasons = [reason],
            ErrorMessage   = error,
        };
}
