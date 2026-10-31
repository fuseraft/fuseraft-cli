using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Protocol;
using fuseraft.Core;
using fuseraft.Infrastructure.Memory;

namespace fuseraft.Cli.Commands.Memory;

// fuseraft memory agents

public sealed class MemoryAgentsSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Print one JSON object to stdout instead of a table (see docs/protocol.md).")]
    public bool Json { get; init; }
}

public sealed class MemoryAgentsCommand : AsyncCommand<MemoryAgentsSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        MemoryAgentsSettings settings,
        CancellationToken cancellationToken)
    {
        var root   = Path.Combine(FuseraftPaths.GlobalMemoryRoot, "agents");
        var agents = new List<WireMemoryAgent>();
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                // Only directories ForAgent itself would create map back to the same store.
                if (!IsStoreName(name)) continue;
                var entries = await MemoryStore.ForAgent(name).LoadAllAsync(cancellationToken);
                if (entries.Count > 0)
                    agents.Add(new WireMemoryAgent(name, entries.Count));
            }
        }

        if (settings.Json)
            return WireProtocol.WriteList(agents);

        if (agents.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No agent memories stored.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]Memories[/]").RightAligned());
        foreach (var a in agents)
            table.AddRow(Markup.Escape(a.Agent), a.Count.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Use [bold]fuseraft memory list --agent <agent>[/] to see an agent's memories.[/]");
        return 0;
    }

    private static bool IsStoreName(string name)
    {
        try { return name == MemoryStore.SafeFileName(name); }
        catch (ArgumentException) { return false; }
    }
}
