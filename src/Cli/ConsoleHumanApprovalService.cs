using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Display;
using fuseraft.Core.Interfaces;
using Spectre.Console;

namespace fuseraft.Cli;

/// <summary>
/// Console-based human approval service — reads y/n and free-form redirects from stdin.
/// </summary>
/// <remarks>
/// Every prompt method wraps its console output and blocking <c>Console.ReadLine()</c> in
/// <see cref="ReplConsole.SuspendSpinner"/>. Without it, a REPL turn's background "fusing…"
/// spinner (which redraws on its own 80ms timer, uncoordinated with anything else touching
/// the console) silently erases and overwrites the prompt on its very next tick — the process
/// is still correctly blocked waiting for input, but a real user sees no visible question, just
/// what looks like an indefinite hang. See <see cref="ReplConsole.SuspendSpinner"/>'s doc
/// comment for how this was diagnosed. Harmless to call from a context with no spinner (e.g. a
/// non-interactive orchestration caller that still wires up console approval).
/// </remarks>
public sealed class ConsoleHumanApprovalService : IHumanApprovalService
{
    public Task<string?> PromptContinueAsync()
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.Markup("[dim]  ↩ Enter or y to continue  ·  type a message to redirect  ·  q to stop:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(input)) return Task.FromResult<string?>(null);
        if (input.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>("\x00");

        // Treat affirmative short inputs as "continue" so that a 'y' typed for
        // a preceding shell-approval prompt that was consumed before the user could
        // respond doesn't accidentally inject "y" as a redirect message.
        if (input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(input);
    }

    public Task<string?> PromptRedirectAsync(string agentName)
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.Markup(
            $"[bold]Redirect {Markup.Escape(agentName)}[/] " +
            $"[dim](Enter to pause session):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }

    public Task<string?> PromptValidatorStuckAsync(string agentName, string validatorName, int consecutiveFailures, string lastError)
    {
        using var _ = ReplConsole.SuspendSpinner();

        const int MaxErrorChars = 800;
        var error = lastError.Length > MaxErrorChars
            ? lastError[..MaxErrorChars] + "\n…(truncated)"
            : lastError;

        AnsiConsole.MarkupLine($"\n[bold {ThemeDetector.Warning}]⏸ HITL intervention required.[/]");
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine($"  Agent:     [bold]{Markup.Escape(agentName)}[/]");
        AnsiConsole.MarkupLine($"  Validator: [bold]{Markup.Escape(validatorName)}[/] ({consecutiveFailures} consecutive failures)\n");
        AnsiConsole.MarkupLine(Markup.Escape(error));
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]Type a message to redirect the agent  ·  press Enter to pause:[/]  ");

        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }

    public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage)
    {
        using var _ = ReplConsole.SuspendSpinner();

        const int MaxReasonChars = 800;
        var reason = blockerMessage.Length > MaxReasonChars
            ? blockerMessage[..MaxReasonChars] + "\n…(truncated)"
            : blockerMessage;

        AnsiConsole.MarkupLine($"\n[bold {ThemeDetector.Warning}]⏸ Agent blocked — intervention required.[/]");
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine($"  Agent: [bold]{Markup.Escape(agentName)}[/]\n");
        AnsiConsole.MarkupLine(Markup.Escape(reason));
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]Type a message to unblock the agent  ·  press Enter to pause:[/]  ");

        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }

    public Task<bool> PromptShellCommandAsync(string command)
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{ThemeDetector.Warning}]⏸ Shell command requested:[/]");
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(command)}[/]");
        AnsiConsole.Markup("[dim]Allow? (y/N):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        var allowed = input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                      input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        AnsiConsole.MarkupLine(allowed ? "[dim]Command allowed.[/]" : "[dim]Command blocked.[/]");
        return Task.FromResult(allowed);
    }

    public Task<bool> PromptToolActionAsync(string plugin, string action, string detail)
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{ThemeDetector.Warning}]⏸ {Markup.Escape(plugin)} action requested:[/]");
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(action)} — {Markup.Escape(detail)}[/]");
        AnsiConsole.Markup("[dim]Allow? (y/N):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        var allowed = input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                      input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        AnsiConsole.MarkupLine(allowed ? "[dim]Action allowed.[/]" : "[dim]Action blocked.[/]");
        return Task.FromResult(allowed);
    }

    public Task<bool> PromptFileWriteAsync(string action, string path, string oldContent, string newContent)
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{ThemeDetector.Warning}]⏸ FileSystem action requested:[/] [dim]{Markup.Escape(action)}[/]");
        AnsiConsole.Write(DiffRenderer.Render(path, oldContent, newContent));
        AnsiConsole.Markup("[dim]Allow? (y/N):[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        var allowed = input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                      input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        AnsiConsole.MarkupLine(allowed ? "[dim]Action allowed.[/]" : "[dim]Action blocked.[/]");
        return Task.FromResult(allowed);
    }

    public Task<string?> PromptPostSessionAsync()
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  Session complete.[/]");
        AnsiConsole.Markup("[dim]  Type a follow-up message to continue  ·  press Enter to exit:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(input)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(input);
    }

    public Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent)
    {
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.MarkupLine(
            $"\n[bold {ThemeDetector.Warning}]⏸ Route approval required.[/]\n" +
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
        using var _ = ReplConsole.SuspendSpinner();

        AnsiConsole.MarkupLine($"\n[bold {ThemeDetector.Warning}]⏸ Magentic Plan Review[/]");
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine(Markup.Escape(planText));
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]Press Enter to approve  ·  type feedback to request revisions:[/]  ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return Task.FromResult<string?>(string.IsNullOrEmpty(input) ? null : input);
    }
}
