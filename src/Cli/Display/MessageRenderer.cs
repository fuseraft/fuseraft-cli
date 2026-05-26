using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Display;

/// <summary>
/// Renders agent messages and session summaries to the terminal using Spectre.Console.
/// </summary>
public static class MessageRenderer
{
    // Palette is assigned round-robin as new agent names appear.
    private static readonly Color[] Palette =
    [
        Color.Aqua, Color.Yellow, Color.Fuchsia, Color.Green,
        Color.Orange1, Color.CornflowerBlue, Color.Plum1,
    ];

    private static readonly Dictionary<string, Color> _colorMap = new(StringComparer.OrdinalIgnoreCase);

    // Banner

    public static void RenderBanner()
    {
        using var stream = typeof(MessageRenderer).Assembly
            .GetManifestResourceStream("fuseraft.Resources.fender.flf");
        var fig = stream is not null
            ? new FigletText(FigletFont.Load(stream), "fuseraft").Color(Color.Aqua)
            : new FigletText("fuseraft").Color(Color.Aqua);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(fig);
        AnsiConsole.MarkupLine("[dim]Multi-Agent Orchestration · Powered by Microsoft Agent Framework[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders the modernized REPL start-up panel in place of the old Figlet banner +
    /// model rule + info line.
    /// </summary>
    public static void RenderReplHeader(
        string modelId,
        string cwd,
        IEnumerable<string> pluginNames,
        string sessionId,
        int memoryCount,
        int skillCount,
        string? eventsPath = null)
    {
        var ver = typeof(MessageRenderer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0";
        // Strip git hash suffix: "1.0.0+abc1234…" → "1.0.0"
        var semver = ver.Contains('+') ? ver[..ver.IndexOf('+')] : ver;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var displayPath = cwd.StartsWith(home, StringComparison.Ordinal)
            ? "~" + cwd[home.Length..]
            : cwd;

        var pluginList = string.Join(", ", pluginNames);

        // Labels are right-padded so values align at column 10.
        var content = new Markup(
            $"[bold]fuseraft[/] [dim]- multi-agent orchestration framework (v{Markup.Escape(semver)})[/]\n" +
            $"\n" +
            $"[dim]Model:[/]    {Markup.Escape(modelId)}\n" +
            $"[dim]Path:[/]     {Markup.Escape(displayPath)}\n" +
            $"[dim]Plugins:[/]  {Markup.Escape(pluginList)}\n" +
            $"[dim]Session:[/]  {Markup.Escape(sessionId)}\n" +
            $"\n" +
            $"[dim]Memories: {memoryCount}, Skills: {skillCount}[/]"
        );

        var panel = new Panel(content)
        {
            Border      = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding     = new Padding(1, 0),
        };

        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        if (eventsPath is not null)
            AnsiConsole.MarkupLine($"[dim]  events: {Markup.Escape(eventsPath)}[/]");

        AnsiConsole.MarkupLine(" [dim]Tip: Use /help to see commands.[/]");
        AnsiConsole.WriteLine();
    }

    // Config summary

    public static void RenderConfigSummary(OrchestrationConfig config, IReadOnlyList<string>? skills = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]{Markup.Escape(config.Name)}[/]")
            .AddColumn(new TableColumn("[bold]Agent[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Model[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Plugins[/]").LeftAligned());

        foreach (var agent in config.Agents)
        {
            var color = GetColor(agent.Name);
            var plugins = agent.Plugins.Count > 0
                ? string.Join(", ", agent.Plugins)
                : "[dim]none[/]";

            table.AddRow(
                $"[{color.ToMarkup()}]{Markup.Escape(agent.Name)}[/]",
                $"[dim]{Markup.Escape(agent.Model.ModelId)}[/]",
                plugins);
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[dim]Selection:[/] {config.Selection.Type}  " +
            $"[dim]Termination:[/] {(config.Termination is not null ? DescribeTermination(config.Termination) : "default")}");

        if (skills is { Count: > 0 })
            AnsiConsole.MarkupLine($"[dim]Skills:[/] {Markup.Escape(string.Join(", ", skills))}");

        AnsiConsole.WriteLine();
    }

    // Task display

    public static void RenderTask(string task)
    {
        AnsiConsole.Write(new Panel(new Markup($"[bold]{Markup.Escape(task.Trim())}[/]"))
        {
            Header      = new PanelHeader(" Task ", Justify.Left),
            Border      = BoxBorder.Heavy,
            BorderStyle = Style.Parse("bold dim"),
            Padding     = new Padding(1, 0)
        });
        AnsiConsole.WriteLine();
    }

    // Agent message

    public static void RenderMessage(AgentMessage message, TimeSpan elapsed, bool showToolCalls = true)
    {
        var color = GetColor(message.AgentName);

        var elapsedStr = elapsed.TotalSeconds > 0.5
            ? $"  [dim]{elapsed.TotalSeconds:0.0}s[/]"
            : string.Empty;

        var usageStr = message.Usage is { } u
            ? $"  [dim]in:{u.InputTokens:N0} out:{u.OutputTokens:N0}[/]"
            : string.Empty;

        var header = $" [bold {color.ToMarkup()}]{Markup.Escape(message.AgentName)}[/]" +
                     $"  [dim]turn {message.TurnIndex + 1}[/]{elapsedStr}{usageStr} ";

        var contentText  = message.Content ?? string.Empty;
        var hasContent   = !string.IsNullOrWhiteSpace(contentText);
        var toolCount    = message.ToolCalls?.Count ?? 0;

        IRenderable body;
        if (showToolCalls && toolCount > 0)
        {
            var rows = new List<IRenderable>
            {
                MarkdownRenderer.Render(contentText),
                new Markup($"\n[dim]── tools ─────────────────────────────[/]"),
            };

            foreach (var tc in message.ToolCalls!)
            {
                var icon    = tc.Succeeded ? "[green]✓[/]" : "[red]✗[/]";
                var name    = $"[dim]{Markup.Escape(tc.Name)}[/]";
                var argPart = tc.ArgsSummary is not null
                    ? $"  [dim]{Markup.Escape(tc.ArgsSummary)}[/]"
                    : string.Empty;
                rows.Add(new Markup($"  {icon} {name}{argPart}"));
            }

            body = new Rows(rows);
        }
        else if (!hasContent && toolCount > 0)
        {
            // Agent produced no summary text but did make tool calls. Show a dim count so
            // the panel is never completely blank — the user can re-run with --tools
            // to see the full tool list.
            body = new Markup($"[dim]({toolCount} tool call{(toolCount == 1 ? "" : "s")} — run with --tools to see details)[/]");
        }
        else
        {
            body = MarkdownRenderer.Render(contentText);
        }

        var panel = new Panel(body)
        {
            Header      = new PanelHeader(header, Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = new Style(color),
            Padding     = new Padding(1, 0),
            Expand      = true
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    // Human-in-the-loop injection

    public static void RenderHumanMessage(AgentMessage message)
    {
        var panel = new Panel(new Markup($"[bold]{Markup.Escape(message.Content)}[/]"))
        {
            Header      = new PanelHeader(" [bold white]Human[/]  [dim]redirecting...[/] ", Justify.Left),
            Border      = BoxBorder.Heavy,
            BorderStyle = Style.Parse("bold white"),
            Padding     = new Padding(1, 0),
            Expand      = true
        };

        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    // Session summary

    public static void RenderSummary(
        IReadOnlyList<AgentMessage> messages,
        bool succeeded,
        TimeSpan totalDuration,
        string? errorMessage = null)
    {
        AnsiConsole.Write(new Rule("[bold]Session Summary[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();

        if (!succeeded && errorMessage is not null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Error: {Markup.Escape(errorMessage)}[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var agentMessages = messages.Where(m => m.Role == "assistant").ToList();

        // Per-agent turn count + tokens
        var agentStats = agentMessages
            .GroupBy(m => m.AgentName)
            .Select(g => (
                Name:         g.Key,
                Turns:        g.Count(),
                InputTokens:  g.Sum(m => m.Usage?.InputTokens ?? 0),
                OutputTokens: g.Sum(m => m.Usage?.OutputTokens ?? 0)
            ));

        bool anyUsage = agentMessages.Any(m => m.Usage is not null);

        var table = new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Agent[/]")
            .AddColumn(new TableColumn("[bold]Turns[/]").RightAligned());

        if (anyUsage)
        {
            table.AddColumn(new TableColumn("[bold]Input tokens[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Output tokens[/]").RightAligned());
        }

        int totalInput = 0, totalOutput = 0;

        foreach (var (name, turns, input, output) in agentStats)
        {
            var color = GetColor(name);
            totalInput  += input;
            totalOutput += output;

            var row = new List<string>
            {
                $"[{color.ToMarkup()}]{Markup.Escape(name)}[/]",
                turns.ToString()
            };

            if (anyUsage)
            {
                row.Add(input > 0 ? $"[dim]{input:N0}[/]" : "[dim]—[/]");
                row.Add(output > 0 ? $"[dim]{output:N0}[/]" : "[dim]—[/]");
            }

            table.AddRow([.. row]);
        }

        table.AddEmptyRow();

        var totalRow = new List<string> { "[bold]Total[/]", $"[bold]{agentMessages.Count}[/]" };

        if (anyUsage)
        {
            totalRow.Add($"[bold]{totalInput:N0}[/]");
            totalRow.Add($"[bold]{totalOutput:N0}[/]");
        }

        table.AddRow([.. totalRow]);
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var durationStr = totalDuration.TotalMinutes >= 1
            ? $"{totalDuration.TotalMinutes:0.0} min"
            : $"{totalDuration.TotalSeconds:0.0}s";

        AnsiConsole.MarkupLine($"[green]✓ Completed[/]  [dim]in {durationStr}[/]");
        AnsiConsole.WriteLine();
    }

    // Helpers

    public static Color GetColor(string agentName)
    {
        if (_colorMap.TryGetValue(agentName, out var c)) return c;
        var assigned = Palette[_colorMap.Count % Palette.Length];
        _colorMap[agentName] = assigned;
        return assigned;
    }

    private static string DescribeTermination(TerminationStrategyConfig t) =>
        t.Type.ToLowerInvariant() switch
        {
            "regex"         => $"regex({t.Pattern}) max={t.MaxIterations}",
            "maxiterations" => $"max={t.MaxIterations}",
            "composite"     => $"composite/{t.Strategies?.Count ?? 0} rules, max={t.MaxIterations}",
            _               => t.Type
        };
}
