using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Display;
using fuseraft.Cli.Commands.Context;
using fuseraft.Cli.Commands.Log;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Commands.Schedule;
using fuseraft.Cli.Commands.Arch;
using fuseraft.Cli.Commands.Knowledge;
using fuseraft.Cli.Commands.Objective;
using fuseraft.Cli.Commands.Graph;
using fuseraft.Cli.Commands.Memory;
using fuseraft.Cli.Commands.Eval;
using fuseraft.Cli.Commands.Skills;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Logging;
using fuseraft.Infrastructure.Plugins;

ConfigureConsoleEncoding();

// Catch crashes on background threads (not covered by Spectre's SetExceptionHandler).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is not Exception ex) return;
    try
    {
        var path = CrashDumper.Write(ex, args);
        AnsiConsole.MarkupLine($"[red]Unhandled crash — dump written to:[/] {Markup.Escape(path)}");
    }
    catch (Exception crashEx) { System.Diagnostics.Debug.WriteLine($"[CrashReporter] {crashEx.Message}"); }
};

// --version: print and exit before Spectre starts.
// Handled here rather than via cfg.SetApplicationVersion because Spectre can
// confuse --version with -v (--verbose) when a default command is registered.
if (args.Any(a => a is "--version" or "-v"))
{
    var ver = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    Console.WriteLine($"fuseraft {ver}");
    return 0;
}

// Pre-parse --verbose, --output, and --vscode before Spectre so these flags
// can configure global state before any services or commands are built.
bool verbose   = args.Any(a => a is "--verbose");
bool vsCodeArg = args.Any(a => a is "--vscode");
if (vsCodeArg)
    OrchestratorBuilder.VsCodeMode = true;
string? outputPath = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "-o" or "--output") { outputPath = args[i + 1]; break; }

// Serilog is configured here and forwarded into Microsoft.Extensions.Logging
// so that all SK and orchestration logs flow through the same pipeline.
// In vscode mode, route ALL console output to stderr so that stdout stays a
// clean newline-delimited JSON stream for the webview panel bridge.
const string LogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// SecretMaskingTextFormatter wraps the standard template formatter so API keys are
// redacted before reaching any sink — console, app.log, and debug sidecar alike.
var maskedFormatter = new SecretMaskingTextFormatter(
    new MessageTemplateTextFormatter(LogTemplate, null));

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        formatter: maskedFormatter,
        standardErrorFromLevel: vsCodeArg ? LogEventLevel.Verbose : null);

// Always write Warning+ to .fuseraft/logs/app.log so store-corruption and other
// runtime warnings survive past the terminal session.
logConfig = logConfig.WriteTo.File(
    formatter: maskedFormatter,
    path: FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAppLog, FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory())),
    restrictedToMinimumLevel: LogEventLevel.Warning,
    fileSizeLimitBytes: 5_000_000,
    rollOnFileSizeLimit: true,
    retainedFileCountLimit: 3);

// When --verbose and --output are both set, write the debug log to a sidecar file
// (<output>.debug.log) so the transcript file contains only clean session content.
if (verbose && outputPath is not null)
{
    var logDir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
    logConfig = logConfig.WriteTo.File(
        formatter: maskedFormatter,
        path: outputPath + ".debug.log");
}

Log.Logger = logConfig.CreateLogger();

// Service registration
var services = new ServiceCollection();
services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSerilog(Log.Logger, dispose: false);
    logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
});

services.AddSingleton(sp => new PluginRegistry(sp.GetRequiredService<ILoggerFactory>()).RegisterDefaults());
services.AddSingleton<ISessionStore, JsonSessionStore>();

// Commands are resolved via DI, register them so Spectre can inject dependencies.
services.AddTransient<RunCommand>();
services.AddTransient<PluginsCommand>();
services.AddTransient<ShowConfigCommand>();
services.AddTransient<ValidateConfigCommand>();
services.AddTransient<SessionsCommand>();
services.AddTransient<InitCommand>();
services.AddTransient<ContextAddCommand>();
services.AddTransient<ContextListCommand>();
services.AddTransient<ContextRemoveCommand>();
services.AddTransient<ReplCommand>();
services.AddTransient<ScheduleAddCommand>();
services.AddTransient<ScheduleListCommand>();
services.AddTransient<ScheduleRemoveCommand>();
services.AddTransient<ScheduleRunCommand>();
services.AddTransient<SkillsAddCommand>();
services.AddTransient<SkillsListCommand>();
services.AddTransient<SkillsRemoveCommand>();
services.AddTransient<SkillsCurationLogCommand>();
services.AddTransient<LogEventsCommand>();
services.AddTransient<LogReplCommand>();
services.AddTransient<LogAppCommand>();
services.AddTransient<UpdateCommand>();
services.AddTransient<GraphBuildCommand>();
services.AddTransient<MemoryReviewCommand>();
services.AddTransient<ArchCheckCommand>();
services.AddTransient<KnowledgeGcCommand>();
services.AddTransient<ObjectiveCreateCommand>();
services.AddTransient<ObjectiveListCommand>();
services.AddTransient<ObjectiveStatusCommand>();
services.AddTransient<EvalCommand>();
services.AddTransient<EvalInitCommand>();

// Use CommandApp<ReplCommand> so bare `fuseraft` drops straight into the REPL.
var registrar = new ServiceCollectionRegistrar(services);
var app = new CommandApp<ReplCommand>(registrar);

// MinVer stamps the full semver (including pre-release and git hash) into
// AssemblyInformationalVersionAttribute at build time — no manual file needed.
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

app.Configure(cfg =>
{
    cfg.SetApplicationName("fuseraft");
    cfg.SetApplicationVersion(version);

    var helpStyle = ThemeDetector.HelpStyle;
    if (helpStyle is not null)
        cfg.Settings.HelpProviderStyles = helpStyle;
    cfg.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteLine();

        if (ex is CommandParseException or CommandRuntimeException { InnerException: null })
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"[grey]Run [{ThemeDetector.Human}]fuseraft --help[/] for usage information.[/]");
            return 1;
        }

        // Write crash dump before printing the exception so the path is visible even
        // if the terminal scrolls away from the stack trace.
        string? dumpPath = null;
        try { dumpPath = CrashDumper.Write(ex, args); }
        catch (Exception crashEx) { System.Diagnostics.Debug.WriteLine($"[CrashReporter] {crashEx.Message}"); }

        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);

        // Walk the exception chain for API response body — helps diagnose 400/4xx errors.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.ClientModel.ClientResultException cre)
            {
                var body = cre.GetRawResponse()?.Content.ToString();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    AnsiConsole.MarkupLine($"[{ThemeDetector.Warning}]API response body:[/]");
                    AnsiConsole.WriteLine(body);
                }
                break;
            }
        }

        if (dumpPath is not null)
            AnsiConsole.MarkupLine($"[dim]Crash dump: {Markup.Escape(dumpPath)}[/]");

        return 1;
    });

    // Register "run" explicitly so `fuseraft run "task"` also works.
    cfg.AddCommand<RunCommand>("run")
        .WithDescription("Run an orchestration session with the agent team.")
        .WithExample(["run", "\"Build a REST API in Go with JWT auth\""])
        .WithExample(["run", "--config", "config/examples/devops-team.json", "\"Deploy to staging\""]);

    cfg.AddCommand<PluginsCommand>("plugins")
        .WithDescription("List all registered plugins and their functions.")
        .WithExample(["plugins"])
        .WithExample(["plugins", "--plugin", "Git"]);

    cfg.AddCommand<ShowConfigCommand>("config")
        .WithDescription("Display an orchestration configuration as rich tables.")
        .WithAlias("show-config")
        .WithExample(["config"])
        .WithExample(["config", ".fuseraft/config/examples/devops-team.json"])
        .WithExample(["config", "--list"]);

    cfg.AddCommand<ValidateConfigCommand>("validate")
        .WithDescription("Validate a configuration file and report all issues.")
        .WithExample(["validate", ".fuseraft/config/orchestration.yaml"])
        .WithExample(["validate", ".fuseraft/config/my-team.json", "--strict"]);

    cfg.AddCommand<SessionsCommand>("sessions")
        .WithDescription("List, inspect, or delete persisted session checkpoints.")
        .WithExample(["sessions"])
        .WithExample(["sessions", "--all"])
        .WithExample(["sessions", "--delete", "a1b2c3d4"])
        .WithExample(["sessions", "--delete", "all"])
        .WithExample(["sessions", "--prune"])
        .WithExample(["sessions", "--cleanup", "--older-than", "30d"])
        .WithExample(["sessions", "--cleanup", "--older-than", "2w", "--project", "brewer"]);

    cfg.AddCommand<InitCommand>("init")
        .WithDescription("Generate a ready-to-run orchestration config from an interactive wizard.")
        .WithExample(["init"])
        .WithExample(["init", ".fuseraft/config/my-team.json"])
        .WithExample(["init", "--template", "dev-team", "--model", "claude-sonnet-4-5"])
        .WithExample(["init", "--template", "minimal", "--no-interactive"]);

    cfg.AddCommand<ReplCommand>("repl")
        .WithDescription("Start an interactive REPL chat session with a single model (no config needed).")
        .WithExample(["repl"])
        .WithExample(["repl", "--model", "gpt-4o"])
        .WithExample(["repl", "--model", "claude-sonnet-4-5", "--system", "You are a helpful coding assistant."]);

    cfg.AddBranch("context", branch =>
    {
        branch.SetDescription("Manage reference material available to all agents in a session.");

        branch.AddCommand<ContextAddCommand>("add")
            .WithDescription("Import a file or directory into the session context store.")
            .WithExample(["context", "add", "~/docs/architecture.pdf"])
            .WithExample(["context", "add", "~/data/schema.sql", "--name", "db-schema"])
            .WithExample(["context", "add", "~/specs/", "--name", "specs", "--description", "Product specifications"]);

        branch.AddCommand<ContextListCommand>("list")
            .WithDescription("List all imported context items.")
            .WithExample(["context", "list"]);

        branch.AddCommand<ContextRemoveCommand>("remove")
            .WithDescription("Remove a context item and delete its copied files.")
            .WithExample(["context", "remove", "db-schema"]);
    });

    cfg.AddBranch("schedule", branch =>
    {
        branch.SetDescription("Create and run scheduled fuseraft sessions via cron expressions.");

        branch.AddCommand<ScheduleAddCommand>("add")
            .WithDescription("Create a new scheduled job.")
            .WithExample(["schedule", "add", "nightly-audit", "--cron", "0 2 * * *", "--task", "Run a security audit and report findings"])
            .WithExample(["schedule", "add", "weekly-report", "--cron", "0 9 * * 1", "--task", "Generate a weekly status report", "--config", "config/report.yaml"]);

        branch.AddCommand<ScheduleListCommand>("list")
            .WithDescription("List all scheduled jobs.")
            .WithExample(["schedule", "list"]);

        branch.AddCommand<ScheduleRemoveCommand>("remove")
            .WithDescription("Remove a scheduled job.")
            .WithExample(["schedule", "remove", "nightly-audit"]);

        branch.AddCommand<ScheduleRunCommand>("run")
            .WithDescription("Execute all due jobs, or force-run a specific job by name.")
            .WithExample(["schedule", "run"])
            .WithExample(["schedule", "run", "--name", "nightly-audit"])
            .WithExample(["schedule", "run", "--dry-run"]);
    });

    cfg.AddBranch("skills", branch =>
    {
        branch.SetDescription("Manage global skills available to all agent sessions.");

        branch.AddCommand<SkillsAddCommand>("add")
            .WithDescription("Copy a skill into ~/.fuseraft/skills and add it to the search index.")
            .WithExample(["skills", "add", "../skills/sandbox-test"])
            .WithExample(["skills", "add", "~/my-skills/triage"]);

        branch.AddCommand<SkillsListCommand>("list")
            .WithDescription("List all installed global skills.")
            .WithExample(["skills", "list"]);

        branch.AddCommand<SkillsRemoveCommand>("remove")
            .WithDescription("Remove a global skill and drop it from the search index.")
            .WithExample(["skills", "remove", "triage"]);

        branch.AddCommand<SkillsCurationLogCommand>("curation-log")
            .WithDescription("View the skill curation log (~/.fuseraft/skill-curation.jsonl).")
            .WithExample(["skills", "curation-log"])
            .WithExample(["skills", "curation-log", "--last", "20"])
            .WithExample(["skills", "curation-log", "--outcome", "failed"]);
    });

    cfg.AddBranch("log", branch =>
    {
        branch.SetDescription("View fuseraft log files.");

        branch.AddCommand<LogEventsCommand>("events")
            .WithDescription("View orchestration event logs (.fuseraft/sessions/{id}/events.jsonl).")
            .WithExample(["log", "events"])
            .WithExample(["log", "events", "--last", "50"])
            .WithExample(["log", "events", "--event", "session_error"])
            .WithExample(["log", "events", "--session", "abc123"]);

        branch.AddCommand<LogReplCommand>("repl")
            .WithDescription("View the REPL event log (.fuseraft/logs/repl_events.jsonl).")
            .WithExample(["log", "repl"])
            .WithExample(["log", "repl", "--last", "50"])
            .WithExample(["log", "repl", "--event", "command"]);

        branch.AddCommand<LogAppCommand>("app")
            .WithDescription("View the application log (.fuseraft/logs/app.log).")
            .WithExample(["log", "app"])
            .WithExample(["log", "app", "--last", "100"])
            .WithExample(["log", "app", "--level", "err"]);
    });

    cfg.AddCommand<UpdateCommand>("update")
        .WithDescription("Fetch the latest fuseraft release from GitHub and replace the running binary.")
        .WithExample(["update"])
        .WithExample(["update", "--check"]);

    cfg.AddBranch("graph", branch =>
    {
        branch.SetDescription("Repository semantic graph — index and query symbols across the codebase.");

        branch.AddCommand<GraphBuildCommand>("build")
            .WithDescription("Scan the project and build (or rebuild) the repository semantic graph.")
            .WithExample(["graph", "build"])
            .WithExample(["graph", "build", "--dir", "src/"])
            .WithExample(["graph", "build", "--output", ".fuseraft/state/repository.graph"]);
    });

    cfg.AddBranch("memory", branch =>
    {
        branch.SetDescription("Repository memory — cross-session patterns extracted from evidence.");

        branch.AddCommand<MemoryReviewCommand>("review")
            .WithDescription("Review candidate repository memories and approve or reject them.")
            .WithExample(["memory", "review"])
            .WithExample(["memory", "review", "--all"]);
    });

    cfg.AddBranch("objective", branch =>
    {
        branch.SetDescription("Long-horizon objective tracking across sessions.");

        branch.AddCommand<ObjectiveCreateCommand>("create")
            .WithDescription("Create a new long-horizon objective.")
            .WithExample(["objective", "create", "--title", "Ship knowledge layer", "--description", "Implement all gaps"])
            .WithExample(["objective", "create", "--title", "Refactor auth", "--tasks", "Design,Implement,Test"]);

        branch.AddCommand<ObjectiveListCommand>("list")
            .WithDescription("List objectives, optionally filtered by status.")
            .WithExample(["objective", "list"])
            .WithExample(["objective", "list", "--status", "Active"]);

        branch.AddCommand<ObjectiveStatusCommand>("status")
            .WithDescription("Show detailed status and progress for a specific objective.")
            .WithExample(["objective", "status", "OBJ-0001"]);
    });

    cfg.AddBranch("arch", branch =>
    {
        branch.SetDescription("Architecture drift detection — check layer boundary compliance.");

        branch.AddCommand<ArchCheckCommand>("check")
            .WithDescription("Scan source files for architecture layer violations.")
            .WithExample(["arch", "check"])
            .WithExample(["arch", "check", "--manifest", ".fuseraft/architecture.yaml"])
            .WithExample(["arch", "check", "--dir", "src/"]);
    });

    cfg.AddBranch("knowledge", branch =>
    {
        branch.SetDescription("Knowledge lifecycle management — archive, decay, and prune stale artifacts.");

        branch.AddCommand<KnowledgeGcCommand>("gc")
            .WithDescription("Run knowledge lifecycle policies (dry-run by default; --apply to commit changes).")
            .WithExample(["knowledge", "gc"])
            .WithExample(["knowledge", "gc", "--apply"])
            .WithExample(["knowledge", "gc", "--apply", "--lifecycle", ".fuseraft/knowledge/lifecycle.yaml"]);
    });

    cfg.AddBranch("eval", branch =>
    {
        branch.SetDescription("Run and manage eval suites against agent teams.");

        branch.AddCommand<EvalCommand>("run")
            .WithDescription("Run an eval suite and report pass/fail per case.")
            .WithExample(["eval", "run", ".fuseraft/evals/suite.yaml"])
            .WithExample(["eval", "run", ".fuseraft/evals/suite.yaml", "--filter", "smoke"])
            .WithExample(["eval", "run", ".fuseraft/evals/suite.yaml", "--output", "results.jsonl"])
            .WithExample(["eval", "run", ".fuseraft/evals/suite.yaml", "--ci"]);

        branch.AddCommand<EvalInitCommand>("init")
            .WithDescription("Scaffold a new eval suite YAML with annotated example cases.")
            .WithExample(["eval", "init"])
            .WithExample(["eval", "init", ".fuseraft/evals/my-suite.yaml"])
            .WithExample(["eval", "init", "--name", "Smoke Tests", "--config", ".fuseraft/config/orchestration.yaml"])
            .WithExample(["eval", "init", "--no-interactive"]);
    });
});

try
{
    return app.Run(args);
}
finally
{
    await Log.CloseAndFlushAsync();
}

static void ConfigureConsoleEncoding()
{
    try { Console.OutputEncoding = Encoding.UTF8; }
    catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { }

    try { Console.InputEncoding = Encoding.UTF8; }
    catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { }
}
