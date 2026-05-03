using fuseraft.Core.Interfaces;
using Spectre.Console;

namespace fuseraft.Cli;

/// <summary>
/// Console-based human approval service — reads y/n and free-form redirects from stdin.
/// </summary>
public sealed class ConsoleHumanApprovalService : IHumanApprovalService
{
    public Task<string?> PromptContinueAsync()
    {
        AnsiConsole.Markup("[dim]  ↩ Enter to continue  ·  type a message to redirect  ·  q to stop:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(input)) return Task.FromResult<string?>(null);
        if (input.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>("\x00");

        return Task.FromResult<string?>(input);
    }

    public Task<string?> PromptRedirectAsync(string agentName)
    {
        AnsiConsole.Markup(
            $"[bold]Redirect {Markup.Escape(agentName)}[/] " +
            $"[dim](Enter to abort session):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }

    public Task<bool> PromptShellCommandAsync(string command)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]⏸ Shell command requested:[/]");
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(command)}[/]");
        AnsiConsole.Markup("[dim]Allow? (y/N):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        var allowed = input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                      input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            AnsiConsole.MarkupLine("[dim]Command blocked.[/]");
        return Task.FromResult(allowed);
    }

    public Task<string?> PromptPostSessionAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  Session complete.[/]");
        AnsiConsole.Markup("[dim]  Type a follow-up message to continue  ·  press Enter to exit:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(input)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(input);
    }

    public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent)
    {
        AnsiConsole.MarkupLine(
            $"\n[bold yellow]⏸ Route approval required.[/]\n" +
            $"  From:    [bold]{Markup.Escape(sourceAgent)}[/]\n" +
            $"  To:      [bold]{Markup.Escape(targetAgent)}[/]\n" +
            $"  Keyword: [bold]{Markup.Escape(keyword)}[/]\n");
        AnsiConsole.Markup("[dim]Approve? (y/N):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult(
            input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    public Task<string?> PromptPlanReviewAsync(string planText)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]⏸ Magentic Plan Review[/]");
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine(Markup.Escape(planText));
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]Press Enter to approve  ·  type feedback to request revisions:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }
}
