using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core.Models.Config;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /mcp
    // -------------------------------------------------------------------------

    private const string McpCategoryPrefix = "mcp:";

    private static async Task<CommandResult> CmdMcpAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var verb  = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest  = parts.Length > 1 ? parts[1] : string.Empty;

        return verb switch
        {
            ""       => CmdMcpList(ctx),
            "add"    => await CmdMcpAddAsync(ctx, rest, cancellationToken),
            "remove" => CmdMcpRemove(ctx, rest),
            _        => Unknown(),
        };

        CommandResult Unknown()
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /mcp | /mcp add [[--session-only]] | /mcp remove <name>");
            return CommandResult.Continue;
        }
    }

    private static CommandResult CmdMcpList(ReplSessionContext ctx)
    {
        var servers = ctx.ToolsByCategory
            .Where(kv => kv.Key.StartsWith(McpCategoryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (servers.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No MCP servers connected. Use /mcp add to connect one.[/]");
            return CommandResult.Continue;
        }

        AnsiConsole.MarkupLine($"[dim]{servers.Count} MCP server(s) connected:[/]");
        foreach (var (category, tools) in servers)
        {
            var name = category[McpCategoryPrefix.Length..];
            AnsiConsole.MarkupLine($"  [bold cyan]{Markup.Escape(name)}[/] [dim]({tools.Count} tool(s))[/]");
            foreach (var t in tools)
                AnsiConsole.MarkupLine($"    [dim]·[/] {Markup.Escape(t.Name)}");
        }
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdMcpAddAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var sessionOnly = arg.Trim().Equals("--session-only", StringComparison.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine("[bold]Add MCP server[/]");

        var name = AnsiConsole.Prompt(new TextPrompt<string>("[dim]Server name[/]").PromptStyle("white"));
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AnsiConsole.MarkupLine("[red]✗ Server name is required.[/]");
            return CommandResult.Continue;
        }
        if (ctx.ToolsByCategory.ContainsKey($"{McpCategoryPrefix}{name}"))
        {
            AnsiConsole.MarkupLine($"[red]✗ A server named '{Markup.Escape(name)}' is already connected. Use /mcp remove first.[/]");
            return CommandResult.Continue;
        }

        var transport = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[dim]Transport[/]")
                .AddChoices("stdio", "http"));

        McpServerConfig config;
        if (transport == "stdio")
        {
            var command = AnsiConsole.Prompt(new TextPrompt<string>("[dim]Command[/] [dim](e.g. npx)[/]").PromptStyle("white"));
            var argsLine = AnsiConsole.Prompt(
                new TextPrompt<string>("[dim]Arguments[/] [dim](space-separated, blank for none)[/]")
                    .AllowEmpty()
                    .PromptStyle("white"));
            config = new McpServerConfig
            {
                Name      = name,
                Transport = "stdio",
                Command   = command.Trim(),
                Args      = argsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            };
        }
        else
        {
            var url = AnsiConsole.Prompt(new TextPrompt<string>("[dim]URL[/]").PromptStyle("white"));
            config = new McpServerConfig
            {
                Name      = name,
                Transport = "http",
                Url       = url.Trim(),
            };
        }

        AnsiConsole.MarkupLine($"[dim]Connecting to '{Markup.Escape(name)}'…[/]");
        List<AIFunction> tools;
        try
        {
            ctx.McpManager ??= new McpSessionManager();
            var (_, connectedTools) = await ctx.McpManager.ConnectSingleAsync(config, cancellationToken);
            tools = connectedTools.ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not connect to '{Markup.Escape(name)}':[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        ctx.ToolsByCategory[$"{McpCategoryPrefix}{name}"] = tools;

        // Rebuild the client so function-invocation middleware is attached even if this REPL
        // session started with zero tool categories (e.g. --no-tools) — same pattern /model
        // already uses when switching to a model with a different tool-availability state.
        var hasTools = ctx.GetActiveTools().Count > 0;
        ctx.Client     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools);
        ctx.StepClient = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
        ctx.ChatOptions = ctx.BuildChatOptions();

        AnsiConsole.MarkupLine($"[green]Connected '{Markup.Escape(name)}' — {tools.Count} tool(s) available.[/]");

        if (!sessionOnly)
        {
            var saved = ReplMcpServerStore.Load();
            saved.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            saved.Add(config);
            ReplMcpServerStore.Save(saved);
            AnsiConsole.MarkupLine($"[dim]Saved — will reconnect automatically on future REPL sessions.[/]");
        }

        return CommandResult.Continue;
    }

    private static CommandResult CmdMcpRemove(ReplSessionContext ctx, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /mcp remove <name>");
            return CommandResult.Continue;
        }

        var category = $"{McpCategoryPrefix}{name}";
        if (!ctx.ToolsByCategory.Remove(category))
        {
            AnsiConsole.MarkupLine($"[yellow]No connected MCP server named '{Markup.Escape(name)}'.[/]");
            return CommandResult.Continue;
        }

        ctx.DisabledCategories.Remove(category);
        ctx.ChatOptions = ctx.BuildChatOptions();

        var saved = ReplMcpServerStore.Load();
        if (saved.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
            ReplMcpServerStore.Save(saved);

        AnsiConsole.MarkupLine(
            $"[green]Removed '{Markup.Escape(name)}'.[/] [dim]Its tools are no longer offered to the model " +
            "(the underlying connection closes when the session ends).[/]");
        return CommandResult.Continue;
    }
}
