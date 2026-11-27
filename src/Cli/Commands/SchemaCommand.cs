using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Team;

namespace fuseraft.Cli.Commands;

/// <summary>Settings for <c>fuseraft schema</c>.</summary>
public sealed class SchemaSettings : CommandSettings
{
    [CommandOption("-o|--output <path>")]
    [Description("Write the schema to this file instead of printing it.")]
    public string? Output { get; init; }
}

/// <summary>Prints the JSON Schema for team files.</summary>
public sealed class SchemaCommand : Command<SchemaSettings>
{
    public override int Execute(CommandContext context, SchemaSettings settings, CancellationToken cancellationToken)
    {
        var schema = TeamSchema.Render();
        if (settings.Output is null)
        {
            Console.Out.Write(schema);
            return 0;
        }
        File.WriteAllText(settings.Output, schema);
        AnsiConsole.MarkupLine($"[green]✓[/] Wrote {Markup.Escape(settings.Output)}");
        return 0;
    }
}
