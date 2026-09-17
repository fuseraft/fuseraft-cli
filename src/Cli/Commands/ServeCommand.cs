using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Serve;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands;

public sealed class ServeSettings : CommandSettings
{
    [CommandOption("-c|--config")]
    [Description("Path to the orchestration config. Defaults to .fuseraft/config/orchestration.yaml.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--work-dir")]
    [Description("Working directory for the daemon's whole lifetime (one daemon serves one project).")]
    public string? WorkDir { get; set; }

    [CommandOption("--http-port")]
    [Description("Fixed port for the MCP (streamable-HTTP) endpoint. Other agents need a stable address to configure, unlike a human reading a printed URL, so this is a fixed default rather than an OS-assigned ephemeral port.")]
    [DefaultValue(8137)]
    public int HttpPort { get; set; } = 8137;

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

        if (settings.WorkDir is not null)
        {
            if (!Directory.Exists(settings.WorkDir))
            {
                AnsiConsole.MarkupLine($"[red]✗ Work directory not found:[/] {Markup.Escape(settings.WorkDir)}");
                return 1;
            }
            Directory.SetCurrentDirectory(settings.WorkDir);
        }

        var unattendedAllow = settings.UnattendedPolicy.Equals("allow", StringComparison.OrdinalIgnoreCase);
        if (!unattendedAllow && !settings.UnattendedPolicy.Equals("deny", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]✗ --unattended-policy must be 'deny' or 'allow', got:[/] {Markup.Escape(settings.UnattendedPolicy)}");
            return 1;
        }

        var host = new ServeHost(
            loggerFactory, pluginRegistry, sessionStore,
            configPath, settings.HttpPort, settings.Socket, unattendedAllow,
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
