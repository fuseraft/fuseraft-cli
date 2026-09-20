using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    internal static async Task<CommandResult> HandleAsync(
        ReplSessionContext ctx, string command, string arg, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "/exit":       return CommandResult.Exit;
            case "/help":       PrintHelp(ctx.JsonMode); return CommandResult.Continue;
            case "/clear":      return await CmdClearAsync(ctx);
            case "/system":     return CmdSystem(ctx, arg);
            case "/tools":      return await CmdToolsAsync(ctx, arg);
            case "/paste":      return CmdPaste(ctx.JsonMode);
            case "/save":       return await CmdSaveAsync(ctx, arg);
            case "/history":    CmdHistory(ctx); return CommandResult.Continue;
            case "/replay":     return CmdReplay(ctx, arg);
            case "/context":    await CmdContextAsync(ctx); return CommandResult.Continue;
            case "/provider":   return await CmdProviderAsync(ctx, arg);
            case "/plan":       return await CmdPlanAsync(ctx, arg);
            case "/execute":    return await CmdExecuteAsync(ctx);
            case "/resume":     return CmdResume(ctx);
            case "/recover":    return CmdRecover(ctx);
            case "/events":     await CmdEventsAsync(ctx, arg); return CommandResult.Continue;
            case "/safe-mode":    return await CmdSafeModeAsync(ctx, arg);
            case "/hitl":         return await CmdHitlAsync(ctx, arg);
            case "/adversarial":  return CmdAdversarial(ctx, arg);
            case "/assist":       return await CmdAssistAsync(ctx, cancellationToken);
            case "/memory":     return await CmdMemoryAsync(ctx, arg, cancellationToken);
            case "/max-tokens": return CmdMaxTokens(ctx, arg);
            case "/temperature": return CmdTemperature(ctx, arg);
            case "/top-p":       return CmdTopP(ctx, arg);
            case "/seed":        return CmdSeed(ctx, arg);
            case "/compact":    return await CmdCompactAsync(ctx, arg, cancellationToken);
            case "/explore":    return await CmdExploreAsync(ctx, arg, cancellationToken);
            case "/locate":     return await CmdLocateAsync(ctx, arg, cancellationToken);
            case "/delegate":   return await CmdDelegateAsync(ctx, arg, cancellationToken);
            case "/agents":     return CmdAgents(ctx);
            case "/agent":      return await CmdAgentAsync(ctx, arg, cancellationToken);
            case "/sessions":      await CmdSessionsAsync(ctx.JsonMode, cancellationToken); return CommandResult.Continue;
            case "/fork":          return await CmdForkAsync(ctx, arg, cancellationToken);
            case "/switch":        return await CmdSwitchAsync(ctx, arg, cancellationToken);
            case "/conversation":  CmdConversation(ctx); return CommandResult.Continue;
            case "/rewind":        return await CmdRewindAsync(ctx, arg, cancellationToken);
            case "/model":         return await CmdModelAsync(ctx, arg);
            case "/models":        return await CmdModelsAsync(ctx, cancellationToken);
            case "/reasoning":     return await CmdReasoningAsync(ctx, arg);
            case "/retry":         return CmdRetry(ctx);
            case "/last":          CmdLast(ctx); return CommandResult.Continue;
            case "/snapshot":      await CmdSnapshotAsync(ctx); return CommandResult.Continue;
            case "/run":           return await CmdRunAsync(ctx, arg, cancellationToken);
            case "/undo":          return await CmdUndoAsync(ctx);
            case "/mcp":           return await CmdMcpAsync(ctx, arg, cancellationToken);
            default:
                AnsiConsole.MarkupLine(
                    $"[yellow]Unknown command:[/] {Markup.Escape(command)}  [dim](type /help for commands)[/]");
                return CommandResult.Continue;
        }
    }

    // -------------------------------------------------------------------------
    // Help
    // -------------------------------------------------------------------------

    // Single source of truth for both the console (Grid) and VS Code JSON-bridge (Markdown)
    // renderings of /help — previously two hand-maintained copies that had already drifted
    // (the JSON copy was missing /paste, /provider setup, and /events stats). One list, two
    // renderers below, means a command added to one surface can no longer be forgotten on
    // the other.
    private readonly record struct HelpEntry(string Command, string Description);
    private readonly record struct HelpSection(string Title, HelpEntry[] Entries);

    private static readonly HelpSection[] HelpSections =
    [
        new("Shell", [
            new("!<command>", "Run a shell command directly (e.g. !git status) — not sent to the model, not added to conversation history"),
            new("!!", "Repeat the last ! command"),
            new("!cd <dir>", "Change the shell escape's working directory (persists across ! commands); !cd, !cd -, and !cd ~ also work"),
        ]),
        new("Session", [
            new("/help", "Show this help"),
            new("/sessions", "List resumable sessions with IDs and turn counts"),
            new("/fork", "Snapshot the current session to a new ID so you can branch from this point"),
            new("/fork switch", "Fork and immediately become the fork (continue under the new ID)"),
            new("/switch <id>", "Save the current session and load another saved session in its place"),
            new("/conversation", "List all turns with numbers so you can pick a rewind point"),
            new("/rewind <n>", "Keep turns 1…n and discard the rest"),
            new("/rewind -<n>", "Step back n turns from the current position"),
            new("/retry", "Resend the last message (useful when the response was poor)"),
            new("/last", "Re-print the last assistant response"),
            new("/clear", "Clear conversation history (keeps system prompt)"),
            new("/history", "Show condensed conversation history"),
            new("/replay [n|all]", "Re-display the last n turns in full (default 3) — the same view shown automatically when a session is resumed"),
            new("/assist", "Diagnose the conversation and inject a corrective message"),
            new("/exit", "Exit the REPL (auto-saves memories)"),
        ]),
        new("Orchestration", [
            new("/run <task>", "Run a task using fuseraft run and inject the result as conversation context"),
            new("/run <file>", "Load task from a file and run it (prompts for config if multiple exist)"),
        ]),
        new("Planning", [
            new("/plan <task>", "Create a structured plan (JSON steps, no tool calls)"),
            new("/plan", "Show the current stored plan"),
            new("/execute", "Run each plan step sequentially with postcondition checks"),
            new("/resume", "Retry the halted step and continue remaining steps"),
            new("/recover", "Inject failure context and retry the halted step with agent awareness"),
        ]),
        new("Tools & modes", [
            new("/tools", "List active tools by category"),
            new("/tools disable <category>", "Disable a tool category (FileSystem Shell Search Git Http)"),
            new("/tools enable <category>", "Re-enable a disabled tool category"),
            new("/tools restrict <plugin> <tag…>", "Allow only tools tagged with one of <tag…> for that plugin (e.g. /tools restrict Git read), using the same capability vocabulary as orchestration's AgentConfig.Capabilities"),
            new("/tools unrestrict <plugin>", "Remove a plugin's capability restriction"),
            new("/undo", "Revert files written, patched, copied, moved, or deleted in the most recent turn (repeatable; walks back one turn at a time — not the same as /rewind, which only affects conversation history)"),
            new("/safe-mode", "Show safe mode status"),
            new("/safe-mode on", "Block Shell, Git, Http tools (by owning plugin, including Extended-bucket tools)"),
            new("/safe-mode off", "Restore prior category disables"),
            new("/hitl", "Show HITL (human-in-the-loop) mode status"),
            new("/hitl on", "Require y/N approval before each shell command, and before each FileSystem write/delete, Git write, or write-ish Http call"),
            new("/hitl off", "Run those calls without approval"),
            new("/adversarial", "Show adversarial mode status"),
            new("/adversarial on", "Enable critic agent to review each /execute step"),
            new("/adversarial off", "Disable critic agent"),
            new("/mcp", "List connected MCP servers and their tools"),
            new("/mcp add", "Interactive wizard to connect an MCP server (persists for future sessions)"),
            new("/mcp add --session-only", "Same, but don't persist past this session"),
            new("/mcp remove <name>", "Stop offering a connected server's tools to the model"),
        ]),
        new("Context & model", [
            new("/context", "Show context window usage (actual once a turn has run, else estimated), per-category breakdown, and cumulative session token usage"),
            new("/compact", "Summarise conversation into a handoff doc and reset history"),
            new("/compact <focus>", "Same, but tailor the summary toward the next session's focus"),
            new("/model", "Show current model and reasoning effort"),
            new("/model <id> [effort]", "Switch model; optional effort is provider-specific, e.g. none, low, medium, high, xhigh, max"),
            new("/models", "List models available from the current provider"),
            new("/reasoning", "Show current reasoning effort"),
            new("/reasoning <effort>", "Set reasoning effort for the current model (provider-specific)"),
            new("/max-tokens <n>", "Set max output tokens for each response"),
            new("/max-tokens reset", "Restore provider default max output tokens"),
            new("/temperature <n>", "Set sampling temperature (0.0–2.0, lower = more deterministic)"),
            new("/temperature reset", "Restore provider default temperature"),
            new("/top-p <n>", "Set nucleus sampling top-p (0.0–1.0)"),
            new("/top-p reset", "Restore provider default top-p"),
            new("/seed <n>", "Fix the sampling seed for reproducible output (provider support varies)"),
            new("/seed reset", "Clear the sampling seed"),
            new("/system", "Show current system prompt"),
            new("/system <prompt>", "Set a new system prompt"),
            new("/provider", "Show current provider, model, and API key"),
            new("/provider setup", "Reconfigure provider, model, and API key"),
        ]),
        new("Memory", [
            new("/memory", "List all stored memories"),
            new("/memory show <name>", "Show full body of a memory"),
            new("/memory delete <name>", "Delete a stored memory"),
            new("/memory save", "Extract and save memories from the current session now"),
        ]),
        new("I/O & events", [
            new("/paste", "Enter paste mode (multi-line input; type .done or press Ctrl+D to finish)"),
            new("/save", "Save transcript to repl-<id>.md in the current directory"),
            new("/save <file>", "Save transcript to the specified file"),
            new("/snapshot", "Write a full debug snapshot (context, tools, history, plan) to a temp file"),
            new("/events", "Show session event stats (turns, tool calls, top tools, per-turn actual input/output tokens)"),
            new("/events stats", "Same as /events"),
            new("/explore <query>", "Run a sub-agent exploration loop and return a prose summary"),
            new("/locate <symbol>", "Run a sub-agent symbol lookup; returns path:line result"),
            new("/delegate <task>", "Hand a self-contained subtask to a write-capable sub-agent (files, shell, git) and return its summary"),
            new("/agents", "List user-defined sub-agents (Markdown files in .fuseraft/agents/ or .agents/agents/) and any load problems"),
            new("/agent <name> <task>", "Run one of your sub-agents directly on a task and show its report"),
        ]),
    ];

    private static void PrintHelp(bool jsonMode = false)
    {
        if (jsonMode)
        {
            var sb = new System.Text.StringBuilder("## REPL Commands\n");
            foreach (var section in HelpSections)
            {
                sb.Append("\n### ").Append(section.Title).Append('\n');
                foreach (var entry in section.Entries)
                    sb.Append("- `").Append(entry.Command).Append("` — ").Append(entry.Description).Append('\n');
            }
            ReplJsonBridge.Emit(new { type = "text", text = sb.ToString().TrimEnd() });
            return;
        }

        AnsiConsole.MarkupLine("[bold]REPL commands[/]");
        AnsiConsole.WriteLine();

        foreach (var section in HelpSections)
        {
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(section.Title)}[/]");

            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 4, 0)));
            grid.AddColumn(new GridColumn().Padding(new Padding(0, 0, 0, 0)));
            foreach (var entry in section.Entries)
                grid.AddRow($"[bold cyan]{Markup.Escape(entry.Command)}[/]", Markup.Escape(entry.Description));
            AnsiConsole.Write(grid);
            AnsiConsole.WriteLine();
        }
    }
}
