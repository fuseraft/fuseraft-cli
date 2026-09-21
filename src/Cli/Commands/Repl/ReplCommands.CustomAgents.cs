using System.Text;
using Spectre.Console;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /agents — list user-defined sub-agents
    // -------------------------------------------------------------------------

    private static CommandResult CmdAgents(ReplSessionContext ctx)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agents not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }

        var agents   = ctx.SubAgent.CustomAgents;
        var problems = ctx.AgentProblems;

        if (ctx.JsonMode)
        {
            ReplJsonBridge.Emit(new { type = "text", text = RenderAgentsMarkdown(agents, problems) });
            return CommandResult.Continue;
        }

        if (agents.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No user-defined sub-agents. Add a Markdown file with YAML frontmatter to[/] " +
                                   "[bold].fuseraft/agents/[/][dim] or[/] [bold].agents/agents/[/][dim] (project) or the same under[/] " +
                                   "[bold]~[/][dim] (user) — see docs/subagents.md.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("agent");
            table.AddColumn("scope");
            table.AddColumn("model");
            table.AddColumn("tools");
            table.AddColumn("description");
            foreach (var a in agents)
            {
                table.AddRow(
                    $"[bold cyan]{Markup.Escape(a.Definition.Name)}[/]",
                    a.Definition.Scope,
                    Markup.Escape(a.Model ?? "[default]"),
                    Markup.Escape(DescribeAgentTools(a)),
                    Markup.Escape(Truncate(a.Definition.Description, 80)));
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("[dim]The model can call them itself via subagent_run; run one directly with[/] [bold]/agent <name> <task>[/][dim].[/]");
        }

        foreach (var p in problems)
            AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(p)}[/]");

        return CommandResult.Continue;
    }

    private static string DescribeAgentTools(SubAgentPlugin.CustomAgentInfo a) =>
        a.Definition.Tools is null ? $"read-only ({a.ToolNames.Count})"
        : a.Definition.AllTools    ? $"all ({a.ToolNames.Count})"
        : a.ToolNames.Count == 0   ? "none"
        : string.Join(", ", a.ToolNames);

    private static string RenderAgentsMarkdown(IReadOnlyList<SubAgentPlugin.CustomAgentInfo> agents, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder();
        if (agents.Count == 0)
            sb.Append("No user-defined sub-agents. Add a Markdown file with YAML frontmatter to `.fuseraft/agents/` or `.agents/agents/`.");
        else
        {
            sb.Append("## Sub-agents\n");
            foreach (var a in agents)
                sb.Append("\n- `").Append(a.Definition.Name).Append("` (").Append(a.Definition.Scope).Append(") — ")
                  .Append(a.Definition.Description.ReplaceLineEndings(" ")).Append("  \n  model: ")
                  .Append(a.Model ?? "default").Append(" · tools: ").Append(DescribeAgentTools(a));
        }
        if (problems.Count > 0)
        {
            sb.Append("\n\n### Problems\n");
            foreach (var p in problems) sb.Append("\n- ").Append(p);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        var one = s.ReplaceLineEndings(" ");
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }

    // -------------------------------------------------------------------------
    // /agent <name> <task> — run one directly
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdAgentAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agents not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }

        var parts = (arg ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            var names = ctx.SubAgent.CustomAgents.Count > 0
                ? string.Join(", ", ctx.SubAgent.CustomAgents.Select(a => a.Definition.Name))
                : "(none defined — see /agents)";
            AnsiConsole.MarkupLine($"[yellow]Usage: /agent <name> <task>[/]  [dim]available: {Markup.Escape(names)}[/]");
            return CommandResult.Continue;
        }

        var (name, task) = (parts[0], parts[1]);
        if (!ctx.SubAgent.CustomAgents.Any(a => a.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var names = ctx.SubAgent.CustomAgents.Count > 0
                ? string.Join(", ", ctx.SubAgent.CustomAgents.Select(a => a.Definition.Name))
                : "(none defined)";
            AnsiConsole.MarkupLine($"[yellow]No sub-agent named '{Markup.Escape(name)}'.[/] [dim]Available: {Markup.Escape(names)}[/]");
            return CommandResult.Continue;
        }

        var spinCts       = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask      = spinCts is not null
            ? ReplConsole.RunSpinnerAsync($"{name} working…", spinCts.Token)
            : Task.CompletedTask;
        bool spinStopped  = false;
        bool headerPrinted = false;

        async Task StopSpinner()
        {
            if (spinStopped || spinCts is null) return;
            spinStopped = true;
            spinCts.Cancel();
            await spinTask;
            ReplConsole.ClearSpinnerLine();
        }

        try
        {
            var (_, inputTok, outputTok) = await ctx.SubAgent.RunAgentStreamingAsync(name, task,
                async chunk =>
                {
                    if (!headerPrinted)
                    {
                        headerPrinted = true;
                        await StopSpinner();
                        if (!ctx.JsonMode) AnsiConsole.MarkupLine($"[dim]{Markup.Escape(name)}:[/]");
                    }
                    if (ctx.JsonMode)
                        ReplJsonBridge.Emit(new { type = "token", text = chunk });
                    else
                        await ReplConsole.WriteChunkSmoothAsync(chunk, cancellationToken);
                },
                cancellationToken);
            ctx.CumulativeInputTokens  += inputTok  ?? 0;
            ctx.CumulativeOutputTokens += outputTok ?? 0;

            await StopSpinner();
            if (headerPrinted) { if (!ctx.JsonMode) AnsiConsole.WriteLine(); }
            else AnsiConsole.MarkupLine("[dim](no output)[/]");
            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/agent", agent = name, task });
        }
        catch (OperationCanceledException)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
        }
        catch (Exception ex)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
        }

        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        return CommandResult.Continue;
    }
}
