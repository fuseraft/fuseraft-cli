using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

public sealed class SettingsToolsSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Print every tool category and tool with its default state as one JSON object (see docs/protocol.md).")]
    public bool Json { get; set; }
}

/// <summary>
/// Lists the chat tools new chats start with on or off (<c>repl.plugins</c>, <c>repl.disabledTools</c>) without starting
/// a chat. Skills and MCP servers are listed as categories only: their tools are known once a chat loads them.
/// </summary>
public sealed class SettingsToolsCommand : Command<SettingsToolsSettings>
{
    public override int Execute(CommandContext context, SettingsToolsSettings settings, CancellationToken cancellationToken)
    {
        var repl       = UserConfigStore.Load().Config?.Repl ?? new ReplDefaultsConfig();
        var categories = Catalog(repl, ManagedMcpServers.Merge(ReplMcpServerStore.Load(), ManagedMcpServers.Load(out _)).Select(s => s.Name));
        if (settings.Json)
            return WireProtocol.WriteList(categories);

        foreach (var c in categories)
        {
            var state = c.Enabled ? "[green]on[/]" : "[dim]off[/]";
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(c.Name)}[/]  {state}  [dim]{Markup.Escape(c.Description)}[/]");
            foreach (var t in c.Tools ?? [])
                AnsiConsole.MarkupLine(t.Enabled
                    ? $"    {Markup.Escape(t.Name)}"
                    : $"    [dim]{Markup.Escape(t.Name)} (off)[/]");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Change them with[/] [bold]fuseraft settings set repl.disabledTools Shell,git_push[/][dim], or[/] [bold]repl.plugins[/] [dim]for the optional ones; in a chat,[/] [bold]/tools save[/] [dim]saves what that chat has on.[/]");
        return 0;
    }

    /// <summary>The categories a new chat would have and which start on, by the saved defaults.</summary>
    internal static List<WireToolCategory> Catalog(ReplDefaultsConfig repl, IEnumerable<string> mcpServers)
    {
        var off     = new HashSet<string>(repl.DisabledTools, StringComparer.OrdinalIgnoreCase);
        var plugins = new HashSet<string>(repl.Plugins, StringComparer.OrdinalIgnoreCase);
        var names   = ReplToolCatalog.Categories.Select(c => c.Name)
            .Concat(mcpServers.Select(s => ReplToolCatalog.McpPrefix + s));
        return [.. names.Select(name =>
        {
            var optional = ReplToolCatalog.OptionalPlugins.Contains(name);
            var tools    = ReplToolCatalog.StaticTools(name);
            return new WireToolCategory(
                name, ReplToolCatalog.Describe(name), optional,
                optional ? plugins.Contains(name) : !off.Contains(name),
                tools?.Select(t => new WireTool(t.Name, t.Description, !off.Contains(t.Name))).ToList());
        })];
    }
}
