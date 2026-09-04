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
            case "/context":    await CmdContextAsync(ctx); return CommandResult.Continue;
            case "/provider":   return await CmdProviderAsync(ctx, arg);
            case "/plan":       return await CmdPlanAsync(ctx, arg);
            case "/execute":    return await CmdExecuteAsync(ctx);
            case "/resume":     return CmdResume(ctx);
            case "/recover":    return CmdRecover(ctx);
            case "/events":     await CmdEventsAsync(ctx, arg); return CommandResult.Continue;
            case "/safe-mode":    return await CmdSafeModeAsync(ctx, arg);
            case "/adversarial":  return CmdAdversarial(ctx, arg);
            case "/assist":       return await CmdAssistAsync(ctx, cancellationToken);
            case "/memory":     return await CmdMemoryAsync(ctx, arg, cancellationToken);
            case "/max-tokens": return CmdMaxTokens(ctx, arg);
            case "/compact":    return await CmdCompactAsync(ctx, arg, cancellationToken);
            case "/explore":    return await CmdExploreAsync(ctx, arg, cancellationToken);
            case "/locate":     return await CmdLocateAsync(ctx, arg, cancellationToken);
            case "/delegate":   return await CmdDelegateAsync(ctx, arg, cancellationToken);
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
            default:
                AnsiConsole.MarkupLine(
                    $"[yellow]Unknown command:[/] {Markup.Escape(command)}  [dim](type /help for commands)[/]");
                return CommandResult.Continue;
        }
    }

    // -------------------------------------------------------------------------
    // Help
    // -------------------------------------------------------------------------

    private static void PrintHelp(bool jsonMode = false)
    {
        if (jsonMode)
        {
            ReplJsonBridge.Emit(new { type = "text", text = """
                ## REPL Commands

                ### Session
                - `/help` — Show this help
                - `/sessions` — List resumable sessions with IDs and turn counts
                - `/fork` — Snapshot the current session to a new ID so you can branch from this point
                - `/fork switch` — Fork and immediately become the fork (continue under the new ID)
                - `/switch <id>` — Save the current session and load another saved session in its place
                - `/conversation` — List all turns with numbers so you can pick a rewind point
                - `/rewind <n>` — Keep turns 1…n and discard the rest
                - `/rewind -<n>` — Step back n turns from the current position
                - `/retry` — Resend the last message (useful when the response was poor)
                - `/last` — Re-print the last assistant response
                - `/clear` — Clear conversation history (keeps system prompt)
                - `/history` — Show condensed conversation history
                - `/assist` — Diagnose the conversation and inject a corrective message
                - `/exit` — Exit the REPL (auto-saves memories)

                ### Orchestration
                - `/run <task>` — Run a task using `fuseraft run` and inject the result as context
                - `/run <file>` — Load task from a file and run it (prompts for config if multiple exist)

                ### Planning
                - `/plan <task>` — Create a structured plan (JSON steps, no tool calls)
                - `/plan` — Show the current stored plan
                - `/execute` — Run each plan step sequentially with postcondition checks
                - `/resume` — Retry the halted step and continue remaining steps
                - `/recover` — Inject failure context and retry the halted step with agent awareness

                ### Tools & modes
                - `/tools` — List active tools by category
                - `/tools disable <category>` — Disable a tool category (FileSystem Shell Search Git Http)
                - `/tools enable <category>` — Re-enable a disabled tool category
                - `/safe-mode` — Show safe mode status
                - `/safe-mode on` — Disable Shell, Git, Http tools to prevent mutations
                - `/safe-mode off` — Restore tool categories
                - `/adversarial` — Show adversarial mode status
                - `/adversarial on` — Enable critic agent to review each `/execute` step
                - `/adversarial off` — Disable critic agent

                ### Context & model
                - `/context` — Show context window usage (actual once a turn has run, else estimated), per-category breakdown, and cumulative session token usage
                - `/compact` — Summarise conversation into a handoff doc and reset history
                - `/compact <focus>` — Same, but tailor the summary toward the next session's focus
                - `/model` — Show current model and reasoning effort
                - `/model <id> [effort]` — Switch model; optional effort is provider-specific, e.g. none, low, medium, high, xhigh, max
                - `/models` — List models available from the current provider
                - `/reasoning` — Show current reasoning effort
                - `/reasoning <effort>` — Set reasoning effort for the current model (provider-specific)
                - `/max-tokens <n>` — Set max output tokens for each response
                - `/max-tokens reset` — Restore provider default max output tokens
                - `/system` — Show current system prompt
                - `/system <prompt>` — Set a new system prompt
                - `/provider` — Show current provider, model, and API key

                ### Memory
                - `/memory` — List all stored memories
                - `/memory show <name>` — Show full body of a memory
                - `/memory delete <name>` — Delete a stored memory
                - `/memory save` — Extract and save memories from the current session now

                ### I/O & events
                - `/save` — Save transcript to `repl-<id>.md` in the current directory
                - `/save <file>` — Save transcript to the specified file
                - `/snapshot` — Write a full debug snapshot (context, tools, history, plan) to a temp file
                - `/events` — Show session event stats (turns, tool calls, top tools, per-turn actual input/output tokens)
                - `/explore <query>` — Run a sub-agent exploration loop and return a prose summary
                - `/locate <symbol>` — Run a sub-agent symbol lookup; returns `path:line` result
                - `/delegate <task>` — Hand a self-contained subtask to a write-capable sub-agent (files, shell, git) and return its summary
                """ });
            return;
        }

        AnsiConsole.MarkupLine("[bold]REPL commands[/]");
        AnsiConsole.WriteLine();

        static Grid MakeGrid()
        {
            var g = new Grid();
            g.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 4, 0)));
            g.AddColumn(new GridColumn().Padding(new Padding(0, 0, 0, 0)));
            return g;
        }

        AnsiConsole.MarkupLine("  [dim]Session[/]");
        var session = MakeGrid();
        session.AddRow("[bold cyan]/help[/]",          "Show this help");
        session.AddRow("[bold cyan]/sessions[/]",      "List resumable sessions with IDs and turn counts");
        session.AddRow("[bold cyan]/fork[/]",           "Snapshot the current session to a new ID (branch from this point)");
        session.AddRow("[bold cyan]/fork switch[/]",    "Fork and immediately become the fork (continue under the new ID)");
        session.AddRow("[bold cyan]/switch <id>[/]",    "Save the current session and load another saved session in its place");
        session.AddRow("[bold cyan]/conversation[/]",   "List all turns with numbers so you can pick a rewind point");
        session.AddRow("[bold cyan]/rewind <n>[/]",     "Keep turns 1…n and discard the rest");
        session.AddRow("[bold cyan]/rewind -<n>[/]",    "Step back n turns from the current position");
        session.AddRow("[bold cyan]/retry[/]",           "Resend the last message (useful when the response was poor)");
        session.AddRow("[bold cyan]/last[/]",            "Re-print the last assistant response");
        session.AddRow("[bold cyan]/clear[/]",          "Clear conversation history (keeps system prompt)");
        session.AddRow("[bold cyan]/history[/]",        "Show condensed conversation history");
        session.AddRow("[bold cyan]/assist[/]",         "Diagnose the conversation and inject a corrective message");
        session.AddRow("[bold cyan]/exit[/]",           "Exit the REPL (auto-saves memories)");
        AnsiConsole.Write(session);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Orchestration[/]");
        var orch = MakeGrid();
        orch.AddRow("[bold cyan]/run <task>[/]",  "Run a task via `fuseraft run`; injects result as conversation context");
        orch.AddRow("[bold cyan]/run <file>[/]",  "Load task from a file and run it (prompts for config if multiple exist)");
        AnsiConsole.Write(orch);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Planning[/]");
        var planning = MakeGrid();
        planning.AddRow("[bold cyan]/plan <task>[/]", "Create a structured plan (JSON steps, no tool calls)");
        planning.AddRow("[bold cyan]/plan[/]",         "Show the current stored plan");
        planning.AddRow("[bold cyan]/execute[/]",      "Run each plan step sequentially with postcondition checks");
        planning.AddRow("[bold cyan]/resume[/]",       "Retry the halted step and continue remaining steps");
        planning.AddRow("[bold cyan]/recover[/]",      "Inject failure context and retry the halted step with agent awareness");
        AnsiConsole.Write(planning);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Tools & modes[/]");
        var tools = MakeGrid();
        tools.AddRow("[bold cyan]/tools[/]",                       "List active tools by category");
        tools.AddRow("[bold cyan]/tools disable <category>[/]",    "Disable a tool category (FileSystem Shell Search Git Http)");
        tools.AddRow("[bold cyan]/tools enable <category>[/]",     "Re-enable a disabled tool category");
        tools.AddRow("[bold cyan]/safe-mode[/]",                   "Show safe mode status");
        tools.AddRow("[bold cyan]/safe-mode on[/]",                "Disable Shell, Git, Http tools to prevent mutations");
        tools.AddRow("[bold cyan]/safe-mode off[/]",               "Restore tool categories");
        tools.AddRow("[bold cyan]/adversarial[/]",                 "Show adversarial mode status");
        tools.AddRow("[bold cyan]/adversarial on[/]",              "Enable critic agent to review each /execute step");
        tools.AddRow("[bold cyan]/adversarial off[/]",             "Disable critic agent");
        AnsiConsole.Write(tools);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Context & model[/]");
        var ctx = MakeGrid();
        ctx.AddRow("[bold cyan]/context[/]",           "Show context window usage (actual once a turn has run, else estimated), per-category breakdown, and cumulative session token usage");
        ctx.AddRow("[bold cyan]/compact[/]",            "Summarise conversation into a handoff doc and reset history");
        ctx.AddRow("[bold cyan]/compact <focus>[/]",    "Same, but tailor the summary toward the next session's focus");
        ctx.AddRow("[bold cyan]/model[/]",                          "Show current model and reasoning effort");
        ctx.AddRow("[bold cyan]/model <id> [[effort]][/]",          "Switch model; effort is provider-specific, e.g. none, low, medium, high, xhigh, max");
        ctx.AddRow("[bold cyan]/models[/]",                         "List models available from the current provider");
        ctx.AddRow("[bold cyan]/reasoning[/]",                     "Show current reasoning effort");
        ctx.AddRow("[bold cyan]/reasoning <effort>[/]",            "Set reasoning effort for the current model (provider-specific)");
        ctx.AddRow("[bold cyan]/max-tokens <n>[/]",     "Set max output tokens for each response");
        ctx.AddRow("[bold cyan]/max-tokens reset[/]",   "Restore provider default max output tokens");
        ctx.AddRow("[bold cyan]/system[/]",             "Show current system prompt");
        ctx.AddRow("[bold cyan]/system <prompt>[/]",    "Set a new system prompt");
        ctx.AddRow("[bold cyan]/provider[/]",           "Show current provider, model, and API key");
        ctx.AddRow("[bold cyan]/provider setup[/]",     "Reconfigure provider, model, and API key");
        AnsiConsole.Write(ctx);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Memory[/]");
        var mem = MakeGrid();
        mem.AddRow("[bold cyan]/memory[/]",               "List all stored memories");
        mem.AddRow("[bold cyan]/memory show <name>[/]",   "Show full body of a memory");
        mem.AddRow("[bold cyan]/memory delete <name>[/]", "Delete a stored memory");
        mem.AddRow("[bold cyan]/memory save[/]",          "Extract and save memories from the current session now");
        AnsiConsole.Write(mem);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]I/O & events[/]");
        var io = MakeGrid();
        io.AddRow("[bold cyan]/paste[/]",           "Enter paste mode (multi-line input; type .done or press Ctrl+D to finish)");
        io.AddRow("[bold cyan]/save[/]",             "Save transcript to repl-<id>.md in the current directory");
        io.AddRow("[bold cyan]/save <file>[/]",      "Save transcript to the specified file");
        io.AddRow("[bold cyan]/snapshot[/]",          "Write a full debug snapshot (context, tools, history, plan) to a temp file");
        io.AddRow("[bold cyan]/events[/]",           "Show session event stats (turns, tool calls, top tools, per-turn actual input/output tokens)");
        io.AddRow("[bold cyan]/events stats[/]",     "Same as /events");
        io.AddRow("[bold cyan]/explore <query>[/]",  "Run a sub-agent exploration loop and return a prose summary");
        io.AddRow("[bold cyan]/locate <symbol>[/]",  "Run a sub-agent symbol lookup; returns path:line result");
        io.AddRow("[bold cyan]/delegate <task>[/]",  "Hand a self-contained subtask to a write-capable sub-agent (files, shell, git)");
        AnsiConsole.Write(io);
    }
}
