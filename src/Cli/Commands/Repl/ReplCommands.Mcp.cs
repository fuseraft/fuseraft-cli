using System.Text;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Mcp;

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
            "remove" => await CmdMcpRemoveAsync(ctx, rest),
            "login"  => await CmdMcpLoginAsync(ctx, rest, cancellationToken),
            "logout" => await CmdMcpLogoutAsync(ctx, rest),
            _        => Unknown(),
        };

        CommandResult Unknown()
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /mcp | /mcp add [[--session-only]] | /mcp remove <name> | /mcp login <name> | /mcp logout <name>");
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
                Args      = SplitStdioArgs(argsLine),
            };
        }
        else
        {
            var url = AnsiConsole.Prompt(new TextPrompt<string>("[dim]URL[/]").PromptStyle("white"));

            var auth = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[dim]Authentication[/]")
                    .AddChoices("None", "Header (API key / bearer token)", "OAuth (browser login)"));

            Dictionary<string, string> headers = [];
            McpOAuthConfig? oauth = null;

            if (auth.StartsWith("Header", StringComparison.Ordinal))
            {
                var headerName = AnsiConsole.Prompt(
                    new TextPrompt<string>("[dim]Header name[/]").DefaultValue("Authorization").PromptStyle("white"));
                var headerValue = AnsiConsole.Prompt(
                    new TextPrompt<string>("[dim]Header value[/] [dim](e.g. \"Bearer ${MY_TOKEN}\" — supports ${ENV_VAR})[/]")
                        .PromptStyle("white"));
                headers[headerName.Trim()] = headerValue.Trim();
            }
            else if (auth.StartsWith("OAuth", StringComparison.Ordinal))
            {
                var scopesLine = AnsiConsole.Prompt(
                    new TextPrompt<string>("[dim]Scopes[/] [dim](space-separated, blank for server default)[/]")
                        .AllowEmpty()
                        .PromptStyle("white"));
                oauth = new McpOAuthConfig
                {
                    Scopes = SplitStdioArgs(scopesLine),
                };
                AnsiConsole.MarkupLine("[dim]Your browser will open to authorize this server on connect.[/]");
            }

            config = new McpServerConfig
            {
                Name      = name,
                Transport = "http",
                Url       = url.Trim(),
                Headers   = headers,
                OAuth     = oauth,
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

        ActivateMcpTools(ctx, $"{McpCategoryPrefix}{name}", tools);

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

    // Registers a connected server's tools and rebuilds the client so function-invocation
    // middleware is attached even if this REPL session started with zero tool categories (e.g.
    // --no-tools) — same pattern /model already uses when switching to a model with a different
    // tool-availability state. Shared by /mcp add and /mcp login.
    private static void ActivateMcpTools(ReplSessionContext ctx, string category, List<AIFunction> tools)
    {
        ctx.ToolsByCategory[category] = tools;

        var activeTools = ctx.GetActiveTools();
        var hasTools = activeTools.Count > 0;
        ctx.Client     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ctx.AdaptiveTrimTracker, ctx.Emitter, tools: activeTools);
        ctx.StepClient = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ctx.AdaptiveTrimTracker, ctx.Emitter, ReplTurn.StepIterationLimit, activeTools);
        ctx.ChatOptions = ctx.BuildChatOptions();
    }

    private static async Task<CommandResult> CmdMcpLoginAsync(
        ReplSessionContext ctx, string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /mcp login <name>");
            return CommandResult.Continue;
        }

        var config = ReplMcpServerStore.Load()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No saved MCP server named '{Markup.Escape(name)}'.[/] [dim]Use /mcp add to configure one.[/]");
            return CommandResult.Continue;
        }

        var category = $"{McpCategoryPrefix}{config.Name}";
        if (ctx.ToolsByCategory.ContainsKey(category))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]'{Markup.Escape(config.Name)}' is already connected.[/] " +
                $"[dim]Use /mcp logout {Markup.Escape(config.Name)} first to force a fresh login.[/]");
            return CommandResult.Continue;
        }

        // Reuses any still-valid cached token (e.g. a server that failed to auto-connect at
        // startup for an unrelated reason) — it does not by itself force a new browser prompt.
        // Run /mcp logout first for that.
        AnsiConsole.MarkupLine($"[dim]Logging in to '{Markup.Escape(config.Name)}'…[/]");
        List<AIFunction> tools;
        try
        {
            ctx.McpManager ??= new McpSessionManager();
            var (_, connectedTools) = await ctx.McpManager.ConnectSingleAsync(config, cancellationToken);
            tools = connectedTools.ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not connect to '{Markup.Escape(config.Name)}':[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        ActivateMcpTools(ctx, category, tools);

        AnsiConsole.MarkupLine($"[green]Connected '{Markup.Escape(config.Name)}' — {tools.Count} tool(s) available.[/]");
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdMcpLogoutAsync(ReplSessionContext ctx, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /mcp logout <name>");
            return CommandResult.Continue;
        }

        var config = ReplMcpServerStore.Load()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No saved MCP server named '{Markup.Escape(name)}'.[/]");
            return CommandResult.Continue;
        }

        if (config.OAuth is null || string.IsNullOrWhiteSpace(config.Url))
        {
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(name)}' isn't configured for OAuth — nothing to log out of.[/]");
            return CommandResult.Continue;
        }

        try
        {
            await McpOAuthTokenCache.LogoutAsync(config.Name, config.Url, ApiKeyStoreFactory.Create());
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ Could not clear the cached token for '{Markup.Escape(name)}':[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        // Also tear down the live connection, if any — continuing to use an already-established
        // session after "logging out" would defeat the point of the command.
        var category = $"{McpCategoryPrefix}{config.Name}";
        var wasConnected = ctx.ToolsByCategory.Remove(category);
        if (wasConnected)
        {
            ctx.DisabledCategories.Remove(category);
            ctx.ChatOptions = ctx.BuildChatOptions();
            try { if (ctx.McpManager is not null) await ctx.McpManager.RemoveAsync(config.Name); }
            catch { /* best-effort — the cached token is already cleared, which is the point */ }
        }

        AnsiConsole.MarkupLine(
            $"[green]Logged out of '{Markup.Escape(name)}'.[/] " +
            $"[dim]{(wasConnected ? "Disconnected. " : "")}Use /mcp login {Markup.Escape(name)} to re-authenticate.[/]");
        return CommandResult.Continue;
    }

    // Quote-aware split for the stdio "Arguments" prompt — a bare space.Split would break an
    // argument value containing a space (e.g. a path) into multiple Args entries.
    private static List<string> SplitStdioArgs(string argsLine)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var inToken = false;

        foreach (var c in argsLine)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote   = c;
                inToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(current.ToString()); current.Clear(); inToken = false; }
                continue;
            }
            current.Append(c);
            inToken = true;
        }
        if (inToken) result.Add(current.ToString());
        return result;
    }

    private static async Task<CommandResult> CmdMcpRemoveAsync(ReplSessionContext ctx, string name)
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

        var saved   = ReplMcpServerStore.Load();
        var removed = saved.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (removed.Count > 0)
        {
            saved.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            ReplMcpServerStore.Save(saved);
        }

        // Drop any cached OAuth token along with the saved entry — otherwise re-adding a
        // server later would silently reuse a stale/revoked token from the keychain.
        foreach (var removedServer in removed)
        {
            if (removedServer.OAuth is null || string.IsNullOrWhiteSpace(removedServer.Url)) continue;
            try { await McpOAuthTokenCache.LogoutAsync(removedServer.Name, removedServer.Url, ApiKeyStoreFactory.Create()); }
            catch { /* best-effort cleanup — a stale keychain entry isn't worth failing /mcp remove over */ }
        }

        // Actually tear down the connection (and, for stdio, its child process) instead of just
        // hiding the tools from the model — previously the connection stayed alive, orphaned,
        // for the rest of the session no matter how many times a server was added and removed.
        var disconnected = false;
        try { disconnected = ctx.McpManager is not null && await ctx.McpManager.RemoveAsync(name); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ Tools removed, but disconnecting '{Markup.Escape(name)}' failed:[/] {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine(disconnected
            ? $"[green]Removed '{Markup.Escape(name)}'[/] [dim]and closed its connection.[/]"
            : $"[green]Removed '{Markup.Escape(name)}'.[/] [dim]Its tools are no longer offered to the model.[/]");
        return CommandResult.Continue;
    }
}
