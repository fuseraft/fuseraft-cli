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
    private static readonly Color[] DarkPalette =
    [
        Color.Aqua, Color.Yellow, Color.Fuchsia, Color.Green,
        Color.Orange1, Color.CornflowerBlue, Color.Plum1,
    ];

    // Darker variants used when the terminal has a light background.
    private static readonly Color[] LightPalette =
    [
        Color.Teal, Color.Olive, Color.Purple, Color.Green,
        Color.Maroon, Color.Navy, Color.Grey,
    ];

    private static readonly Dictionary<string, Color> _colorMap = new(StringComparer.OrdinalIgnoreCase);

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
        string? branch = null,
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
        var branchLine = branch is not null
            ? $"[dim]Branch:[/]   {Markup.Escape(branch)}\n"
            : string.Empty;

        var content = new Markup(
            $"[bold]fuseraft[/] [dim]- multi-agent coordination framework (v{Markup.Escape(semver)})[/]\n" +
            $"\n" +
            $"[dim]Model:[/]    {Markup.Escape(modelId)}\n" +
            $"[dim]Path:[/]     {Markup.Escape(displayPath)}\n" +
            $"{branchLine}" +
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
            // No summary text — emit a compact single line instead of a full panel.
            var callWord  = toolCount == 1 ? "call" : "calls";
            var elapsedFmt = elapsed.TotalSeconds > 0.5 ? $"  {elapsed.TotalSeconds:0.0}s" : string.Empty;
            var usageFmt   = message.Usage is { } u2 ? $"  in:{u2.InputTokens:N0} out:{u2.OutputTokens:N0}" : string.Empty;
            AnsiConsole.MarkupLine(
                $"  [bold {color.ToMarkup()}]{Markup.Escape(message.AgentName)}[/]" +
                $"  [dim]turn {message.TurnIndex + 1}{Markup.Escape(elapsedFmt)}{Markup.Escape(usageFmt)}" +
                $"  {toolCount} tool {callWord}[/]");
            return;
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
        var humanColor = ThemeDetector.Human;
        var panel = new Panel(new Markup($"[bold]{Markup.Escape(message.Content)}[/]"))
        {
            Header      = new PanelHeader($" [bold {humanColor}]Human[/]  [dim]redirecting...[/] ", Justify.Left),
            Border      = BoxBorder.Heavy,
            BorderStyle = Style.Parse($"bold {humanColor}"),
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

        var agentMessages = messages.Where(m => m.Role == MessageRole.Assistant).ToList();

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
        var palette  = ThemeDetector.IsLightBackground ? LightPalette : DarkPalette;
        var assigned = palette[_colorMap.Count % palette.Length];
        _colorMap[agentName] = assigned;
        return assigned;
    }

    private static string DescribeTermination(TerminationStrategyConfig t) =>
        t.Type.ToLowerInvariant() switch
        {
            "regex"         => $"regex({t.Pattern}) max={t.MaxIterations}",
            "structured"    => $"structured({DescribeCondition(t.Condition)}) max={t.MaxIterations}",
            "tokenbudget"   => $"tokenbudget({t.MaxTokens} tokens) max={t.MaxIterations}",
            "maxiterations" => $"max={t.MaxIterations}",
            "composite"     => $"composite/{t.Strategies?.Count ?? 0} rules, max={t.MaxIterations}",
            _               => t.Type
        };

    private static string DescribeCondition(StructuredCondition? c)
    {
        if (c is null) return "?";
        if (c.Is is not null)       return $"{c.Field}=={c.Is}";
        if (c.IsNot is not null)    return $"{c.Field}!={c.IsNot}";
        if (c.Contains is not null) return $"{c.Field} contains {c.Contains}";
        if (c.Exists is not null)   return $"{c.Field} {(c.Exists.Value ? "exists" : "absent")}";
        return c.Field;
    }
}
