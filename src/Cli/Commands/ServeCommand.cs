using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Serve;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands;

public sealed class ServeSettings : CommandSettings
{
    [CommandOption("-c|--config")]
    [Description("Path to the orchestration config. Defaults to .fuseraft/config/orchestration.yaml.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--work-dir")]
    [Description("Working directory for the daemon's whole lifetime (one daemon serves one project). Falls back to the config's Security.FileSystemSandboxPath, then the current directory.")]
    public string? WorkDir { get; set; }

    [CommandOption("--http-port")]
    [Description("Port for the MCP (streamable-HTTP) endpoint. Other agents need a stable address to configure, unlike a human reading a printed URL, so this defaults to a fixed-per-project (not OS-assigned ephemeral) port derived from the project path, so two daemons for two different projects don't collide on the same default port.")]
    public int? HttpPort { get; set; }

    [CommandOption("--socket")]
    [Description("Path to the Unix domain socket `fuseraft attach` connects to. Defaults to ~/.fuseraft/run/<project-hash>.sock.")]
    public string? Socket { get; set; }

    [CommandOption("--unattended-policy")]
    [Description("Policy for mutating tool calls (shell/write/git-push/etc.) when no human is attached: 'deny' (default — a task dispatched by another agent with nobody watching is denied) or 'allow' (auto-approve, matching CI-style unattended runs).")]
    [DefaultValue("deny")]
    public string UnattendedPolicy { get; set; } = "deny";

    [CommandOption("--auto-objective")]
    [Description("Objective ID (e.g. OBJ-0001, from `fuseraft objective list`) whose RemainingTasks the daemon pulls from and runs on its own whenever idle, instead of only running tasks it's explicitly dispatched. Repeatable. Off by default — an objective is never worked automatically just because it exists.")]
    public string[]? AutoObjective { get; set; }
}

/// <summary>
/// Starts a long-lived idle-mode daemon: builds the agent team once, then waits for a human
/// (<c>fuseraft attach</c>) or another agent (MCP <c>dispatch_task</c>) to give it work, instead
/// of exiting after a single task the way <c>fuseraft run</c> does. See
/// <see cref="fuseraft.Cli.Serve.ServeHost"/> for the daemon's lifecycle.
/// </summary>
public sealed class ServeCommand(ILoggerFactory loggerFactory, PluginRegistry pluginRegistry, ISessionStore sessionStore)
    : AsyncCommand<ServeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ServeSettings settings, CancellationToken cancellationToken)
    {
        var configPath = Path.GetFullPath(settings.ConfigPath ?? ".fuseraft/config/orchestration.yaml");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Config not found:[/] {Markup.Escape(configPath)}");
            return 1;
        }

        // Mirrors RunCommand's ResolveWorkDir: an explicit --work-dir wins, otherwise fall back
        // to the config's own declared sandbox path rather than silently keeping the launch-time
        // CWD, so a config's relative paths (checkpoint, validation, ChangeTracker) resolve the
        // same way they would under `fuseraft run`.
        var workDir = RunCommand.ResolveWorkDir(settings.WorkDir, configPath, loggerFactory.CreateLogger<ServeCommand>());
        if (workDir is not null)
        {
            if (!Directory.Exists(workDir))
            {
                AnsiConsole.MarkupLine($"[red]✗ Work directory not found:[/] {Markup.Escape(workDir)}");
                return 1;
            }
            Directory.SetCurrentDirectory(workDir);
        }

        var unattendedAllow = settings.UnattendedPolicy.Equals("allow", StringComparison.OrdinalIgnoreCase);
        if (!unattendedAllow && !settings.UnattendedPolicy.Equals("deny", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]✗ --unattended-policy must be 'deny' or 'allow', got:[/] {Markup.Escape(settings.UnattendedPolicy)}");
            return 1;
        }

        // Fail fast on a missing/invalid provider key — the same check `fuseraft run` makes —
        // rather than letting the daemon report "Idle, ready" and only discovering the problem
        // deep inside the first dispatched task.
        try
        {
            var config = OrchestratorConfigLoader.LoadConfig(configPath);
            await ApiKeyValidator.ValidateApiKeysAsync(config);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ API key validation failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // Honor the config's Checkpoint.Mode/Path exactly like `fuseraft run` does — without
        // this, a config that asks for in-memory-only sessions would still have every dispatched
        // task's full history persisted to the global session store.
        var activeStore = RunCommand.BuildActiveStore(configPath, loggerFactory, sessionStore);

        var projectSlug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        var httpPort    = settings.HttpPort ?? FuseraftPaths.DefaultDaemonHttpPort(projectSlug);

        var host = new ServeHost(
            loggerFactory, pluginRegistry, activeStore,
            configPath, httpPort, settings.Socket, unattendedAllow,
            settings.AutoObjective ?? []);

        try
        {
            return await host.RunAsync();
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Failed to start daemon:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
