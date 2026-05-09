using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;

ConfigureWindowsConsoleEncoding();

// Catch crashes on background threads (not covered by Spectre's SetExceptionHandler).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is not Exception ex) return;
    try
    {
        var path = CrashDumper.Write(ex, args);
        AnsiConsole.MarkupLine($"[red]Unhandled crash — dump written to:[/] {Markup.Escape(path)}");
    }
    catch { /* never let the crash reporter itself crash */ }
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

// Pre-parse --verbose and --output before Spectre so the logger can be
// configured at the right level and file sink before any services are built.
bool verbose = args.Any(a => a is "--verbose");
string? outputPath = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "-o" or "--output") { outputPath = args[i + 1]; break; }

// Serilog is configured here and forwarded into Microsoft.Extensions.Logging
// so that all SK and orchestration logs flow through the same pipeline.
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

// Always write Warning+ to .fuseraft/logs/app.log so store-corruption and other
// runtime warnings survive past the terminal session.
logConfig = logConfig.WriteTo.File(
    FuseraftPaths.LocalAppLog,
    restrictedToMinimumLevel: LogEventLevel.Warning,
    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
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
        outputPath + ".debug.log",
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
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

// Use CommandApp<RunCommand> so `fuseraft` with no subcommand defaults to run.
var registrar = new ServiceCollectionRegistrar(services);
var app = new CommandApp<RunCommand>(registrar);

// MinVer stamps the full semver (including pre-release and git hash) into
// AssemblyInformationalVersionAttribute at build time — no manual file needed.
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

app.Configure(cfg =>
{
    cfg.SetApplicationName("fuseraft");
    cfg.SetApplicationVersion(version);
    cfg.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteLine();

        if (ex is CommandParseException or CommandRuntimeException { InnerException: null })
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Run [white]fuseraft --help[/] for usage information.[/]");
            return 1;
        }

        // Write crash dump before printing the exception so the path is visible even
        // if the terminal scrolls away from the stack trace.
        string? dumpPath = null;
        try { dumpPath = CrashDumper.Write(ex, args); }
        catch { /* never let the crash reporter itself crash */ }

        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);

        // Walk the exception chain for API response body — helps diagnose 400/4xx errors.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.ClientModel.ClientResultException cre)
            {
                var body = cre.GetRawResponse()?.Content.ToString();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    AnsiConsole.MarkupLine("[yellow]API response body:[/]");
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
        .WithExample(["config", "config/examples/devops-team.json"])
        .WithExample(["config", "--list"]);

    cfg.AddCommand<ValidateConfigCommand>("validate")
        .WithDescription("Validate a configuration file and report all issues.")
        .WithExample(["validate", "config/orchestration.yaml"])
        .WithExample(["validate", "config/my-team.json", "--strict"]);

    cfg.AddCommand<SessionsCommand>("sessions")
        .WithDescription("List, inspect, or delete persisted session checkpoints.")
        .WithExample(["sessions"])
        .WithExample(["sessions", "--all"])
        .WithExample(["sessions", "--delete", "a1b2c3d4"])
        .WithExample(["sessions", "--delete", "all"]);

    cfg.AddCommand<InitCommand>("init")
        .WithDescription("Generate a ready-to-run orchestration config from an interactive wizard.")
        .WithExample(["init"])
        .WithExample(["init", "config/my-team.json"])
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
});

try
{
    return app.Run(args);
}
finally
{
    await Log.CloseAndFlushAsync();
}

static void ConfigureWindowsConsoleEncoding()
{
    if (!OperatingSystem.IsWindows()) return;

    TrySetConsoleEncoding(() => Console.OutputEncoding = Encoding.UTF8);
    TrySetConsoleEncoding(() => Console.InputEncoding = Encoding.UTF8);
}

static void TrySetConsoleEncoding(Action setEncoding)
{
    try
    {
        setEncoding();
    }
    catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
    {
    }
}
