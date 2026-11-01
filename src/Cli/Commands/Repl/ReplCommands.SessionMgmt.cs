using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /fork
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdForkAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var doSwitch = arg.Equals("switch", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(arg) && !doSwitch)
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /fork argument:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /fork         — snapshot current session to a new ID[/]");
            AnsiConsole.MarkupLine("[dim]       /fork switch  — fork and immediately become the fork[/]");
            return CommandResult.Continue;
        }

        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var forkId = Convert.ToHexString(bytes).ToLowerInvariant();

        var execQueue = ctx.ExecutionQueue.Count > 0
            ? [.. ctx.ExecutionQueue.Select(e => new PlanStepEntry(e.Step, e.Total))]
            : (PlanStepEntry[]?)null;

        var haltedAt = ctx.HaltedAt.HasValue
            ? new PlanStepEntry(ctx.HaltedAt.Value.Step, ctx.HaltedAt.Value.Total)
            : (PlanStepEntry?)null;

        var haltedRemaining = ctx.HaltedRemaining.Count > 0
            ? [.. ctx.HaltedRemaining.Select(e => new PlanStepEntry(e.Step, e.Total))]
            : (PlanStepEntry[]?)null;

        var snapshot = ReplSessionSnapshot.Capture(
            sessionId:       forkId,
            modelId:         ctx.ModelId,
            cwd:             ctx.Cwd,
            turnIndex:       ctx.TurnIndex,
            history:         ctx.History,
            startedAt:       DateTime.UtcNow,
            currentPlan:     ctx.CurrentPlan,
            executionQueue:  execQueue,
            haltedAt:        haltedAt,
            haltedRemaining: haltedRemaining,
            haltedToolCalls: ctx.HaltedToolCalls.Count > 0 ? [.. ctx.HaltedToolCalls] : null,
            recoveryHint:    ctx.RecoveryHint,
            todoItems:       ctx.Todo?.Snapshot() is { Count: > 0 } todoItems ? [.. todoItems] : null,
            goal:            ctx.LastGoal?.ToSaved(),
            title:           ctx.Title,
            autoTitle:       ctx.AutoTitle);

        try
        {
            await ReplSessionSnapshot.SaveAsync(snapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Fork failed:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        if (doSwitch)
        {
            // The original session is already checkpointed on disk from the last turn's
            // auto-save.  Switch the live session to the fork by updating the mutable IDs.
            var prevId        = ctx.SessionId;
            ctx.SessionId     = forkId;
            ctx.StartedAt     = DateTime.UtcNow;
            ctx.Emitter.SetSessionId(forkId);

            if (ctx.JsonMode)
            {
                ReplJsonBridge.Emit(new { type = "text", text =
                    $"## Switched to Fork\n\n" +
                    $"Previous session: **`{prevId}`** (saved)\n\n" +
                    $"Now running as: **`{forkId}`**" });
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Switched to fork:[/] [bold cyan]{Markup.Escape(forkId)}[/]  [dim](was {Markup.Escape(prevId)})[/]");
            }

            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
                { command = "/fork switch", fork_id = forkId, prev_id = prevId, turns = ctx.TurnIndex });
        }
        else
        {
            if (ctx.JsonMode)
            {
                ReplJsonBridge.Emit(new { type = "text", text =
                    $"## Session Forked\n\n" +
                    $"New session ID: **`{forkId}`**\n\n" +
                    $"Resume with: `fuseraft repl --resume {forkId}`\n\n" +
                    $"Or use `/fork switch` to branch and continue as the fork immediately." });
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Forked to:[/] [bold cyan]{Markup.Escape(forkId)}[/]  [dim]({ctx.TurnIndex} turn{(ctx.TurnIndex == 1 ? "" : "s")} copied)[/]");
                AnsiConsole.MarkupLine($"[dim]Resume with:[/] [bold]fuseraft repl --resume {Markup.Escape(forkId)}[/]");
                AnsiConsole.MarkupLine($"[dim]Or:[/] [bold]/fork switch[/] [dim]to branch and continue as the fork right now.[/]");
            }

            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
                { command = "/fork", fork_id = forkId, turns = ctx.TurnIndex });
        }

        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /title
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdTitleAsync(ReplSessionContext ctx, string arg)
    {
        var message = ApplyTitle(ctx, arg);
        if (ctx.TitleUnsaved) await ReplTurn.SaveSnapshotAsync(ctx);

        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "text", text = message });
        else
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");
        return CommandResult.Continue;
    }

    // Commands that can run while a turn is still going: they touch nothing the turn reads or writes, except
    // the HITL flag, which every approver reads afresh per tool call. A one-word entry takes any argument; a
    // longer one is the whole command. The VS Code panel gets this list in the ready event.
    internal static readonly string[] MidTurnCommands = ["/title", "/hitl on", "/hitl off"];

    /// <summary>
    /// Runs <paramref name="line"/> straight away if it's one of the commands that needn't wait for the
    /// running turn, returning what to tell the user; null when it has to wait. Never writes to the console,
    /// which the turn is streaming to.
    /// </summary>
    internal static string? RunMidTurn(ReplSessionContext ctx, string line)
    {
        var parts   = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var arg     = parts.Length > 1 ? parts[1] : string.Empty;
        var whole   = arg.Length > 0 ? $"{command} {arg.ToLowerInvariant()}" : command;
        if (!MidTurnCommands.Any(c => c == whole || (!c.Contains(' ') && c == command))) return null;

        var message = command switch
        {
            // The turn's own snapshot save picks the title up, or the REPL saves it before its next prompt.
            "/title" => ApplyTitle(ctx, arg),
            _        => SetHitl(ctx, on: whole == "/hitl on"),
        };
        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "applied", command = line.Trim(), message });
        return message;
    }

    /// <summary>Shows, sets or (with --clear) removes the title, telling the panel; returns the message for the user.</summary>
    private static string ApplyTitle(ReplSessionContext ctx, string arg)
    {
        string message;
        if (string.IsNullOrWhiteSpace(arg))
        {
            message = (ctx.Title, ctx.AutoTitle) switch
            {
                ({ } title, { } auto) => $"Title: {title} (/title --clear goes back to the generated title, {auto})",
                ({ } title, null)     => $"Title: {title} (/title --clear goes back to the first message)",
                (null, { } auto)      => $"Title: {auto} (generated from the first exchange; /title followed by a name renames it)",
                _                     => "No title — this chat is listed by its first message. Name it with /title followed by a name.",
            };
        }
        else
        {
            ctx.Title = arg.Trim().Equals("--clear", StringComparison.OrdinalIgnoreCase) ? null : NormalizeTitle(arg);
            ctx.TitleUnsaved = true;
            message = ctx.Title is not null ? $"Chat renamed to {ctx.Title}."
                : ctx.AutoTitle is not null ? $"Title cleared — this chat is listed by its generated title, {ctx.AutoTitle}, again."
                : "Title cleared — this chat is listed by its first message again.";
            _ = ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/title", title = ctx.Title });
        }

        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "title", sessionId = ctx.SessionId, title = ctx.Title, autoTitle = ctx.AutoTitle });
        return message;
    }

    /// <summary>Collapses a title to one line; null when nothing is left.</summary>
    internal static string? NormalizeTitle(string? title)
    {
        var collapsed = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }

    // -------------------------------------------------------------------------
    // /switch
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdSwitchAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[dim]Usage: /switch <session-id>[/]");
            AnsiConsole.MarkupLine("[dim]Run /sessions to list available sessions.[/]");
            return CommandResult.Continue;
        }

        var targetId = arg.Trim();
        if (targetId.Equals(ctx.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[dim]Already in this session.[/]");
            return CommandResult.Continue;
        }

        // Checkpoint the current session before leaving it.
        await ReplTurn.SaveSnapshotAsync(ctx);

        var snapshot = await ReplSessionSnapshot.LoadAsync(targetId, cancellationToken);
        if (snapshot is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No saved session found with ID '[bold]{Markup.Escape(targetId)}[/]'.[/]");
            AnsiConsole.MarkupLine("[dim]Run /sessions to list available sessions.[/]");
            return CommandResult.Continue;
        }

        // The sandbox, approvals and project instructions were rooted at this REPL's directory at
        // startup and can't move now, so a session from another directory would have its own
        // paths blocked. --resume at startup moves into the session's directory instead.
        if (ReplCommand.ResumeDirectory(snapshot.Cwd, ctx.Cwd) is { } otherDir)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Session [bold]{Markup.Escape(snapshot.SessionId)}[/] ran in {Markup.Escape(otherDir)}, not here.[/]");
            AnsiConsole.MarkupLine(
                $"[dim]Resume it there:[/] cd {Markup.Escape(otherDir)} && fuseraft --resume {Markup.Escape(snapshot.SessionId)}");
            return CommandResult.Continue;
        }

        // Switching away discards this session's history from the REPL, so save its memories first.
        await ReplTurn.SaveMemoriesBeforeDiscardAsync(ctx, ct: cancellationToken);

        var prevId    = ctx.SessionId;
        var prevModel = ctx.ModelId;

        // Switch model when the target session used a different one.
        if (!snapshot.ModelId.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var activeTools = ctx.GetActiveTools();
            var hasTools  = activeTools.Count > 0;
            var newConfig = ReplFactory.BuildModelConfig(snapshot.ModelId, ctx.UserCfg, connection: ctx.Connection);
            try
            {
                var newClient     = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ctx.AdaptiveTrimTracker, ctx.Emitter, tools: activeTools, limits: ctx.Limits);
                var newStepClient = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ctx.AdaptiveTrimTracker, ctx.Emitter, ReplTurn.StepIterationLimit, activeTools);
                ctx.ModelId     = snapshot.ModelId;
                ctx.ModelConfig = newConfig;
                ctx.Client      = newClient;
                ctx.StepClient  = newStepClient;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ Could not switch to model {Markup.Escape(snapshot.ModelId)}: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"[dim]Keeping current model: {Markup.Escape(ctx.ModelId)}[/]");
            }
        }

        ctx.SessionId = snapshot.SessionId;
        ctx.StartedAt = snapshot.StartedAt;
        ctx.Title     = snapshot.Title;
        ctx.AutoTitle = snapshot.AutoTitle;
        ctx.AutoTitleStarted = false;
        ctx.AutoTitleGeneration++;
        ctx.Emitter.SetSessionId(snapshot.SessionId);

        // Restore history; keep the current system prompt so memories and AGENTS.md
        // stay fresh (same approach as --resume at startup).
        var restored   = snapshot.RestoreHistory();
        var currentSys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        if (restored.Count > 0 && restored[0].Role == ChatRole.System && currentSys is not null)
            restored[0] = currentSys;
        ctx.History.Clear();
        ctx.History.AddRange(restored);

        ctx.TurnIndex              = snapshot.TurnIndex;
        ctx.PrevTurnTokenEstimate  = 0;
        ctx.PrevCtxEstimate        = 0;
        ctx.TurnTokenDeltas.Clear();
        ctx.LastExtractedTurnIndex = snapshot.LastExtractedTurnIndex ?? -1;
        ctx.ContextWarningShown    = false;
        ctx.ResetPlanState();
        ctx.LastGoal = GoalRecord.FromSaved(snapshot.Goal);

        if (snapshot.ExecutionQueue is { Length: > 0 })
            foreach (var e in snapshot.ExecutionQueue)
                ctx.ExecutionQueue.Enqueue((e.Step, e.Total));
        else if (snapshot.PendingPlan is { Length: > 0 })
            ctx.CurrentPlan = snapshot.PendingPlan;

        if (snapshot.HaltedAt is not null)
        {
            ctx.HaltedAt = (snapshot.HaltedAt.Step, snapshot.HaltedAt.Total);
            if (snapshot.HaltedRemaining is { Length: > 0 })
                foreach (var e in snapshot.HaltedRemaining)
                    ctx.HaltedRemaining.Enqueue((e.Step, e.Total));
            ctx.HaltedToolCalls = [.. snapshot.HaltedToolCalls ?? []];
            ctx.RecoveryHint    = snapshot.RecoveryHint;
        }

        var modelChanged = !ctx.ModelId.Equals(prevModel, StringComparison.OrdinalIgnoreCase);

        if (ctx.JsonMode)
        {
            ReplJsonBridge.Emit(new { type = "text", text =
                $"## Switched Session\n\n" +
                $"Now running as: **`{snapshot.SessionId}`** (was `{prevId}`)\n\n" +
                $"Model: {ctx.ModelId} · {snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}" });
            ReplReplay.ShowOnRestore(ctx);
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[dim]Switched to:[/] [bold cyan]{Markup.Escape(snapshot.SessionId)}[/]  " +
                $"[dim](was {Markup.Escape(prevId)})[/]");
            AnsiConsole.MarkupLine(
                $"[dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]" +
                (modelChanged ? $"  [dim](was {Markup.Escape(prevModel)})[/]" : string.Empty));
            AnsiConsole.MarkupLine(
                $"[dim]{snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}[/]");

            ReplReplay.ShowOnRestore(ctx);

            if (ctx.ExecutionQueue.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[dim]  Plan in progress: {ctx.ExecutionQueue.Count} step{(ctx.ExecutionQueue.Count == 1 ? "" : "s")} queued — resuming automatically[/]");
            else if (ctx.CurrentPlan is { Length: > 0 })
                AnsiConsole.MarkupLine(
                    $"[dim]  Pending plan restored ({ctx.CurrentPlan.Length} step{(ctx.CurrentPlan.Length == 1 ? "" : "s")}). Run /execute to start.[/]");

            if (ctx.HaltedAt is not null)
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ Plan halted at step {ctx.HaltedAt.Value.Step.Step} of {ctx.HaltedAt.Value.Total}. Run /recover or /resume.[/]");
        }
        ReplCommands.AnnounceRestoredGoal(ctx);

        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
        {
            command   = "/switch",
            target_id = snapshot.SessionId,
            prev_id   = prevId,
            turns     = snapshot.TurnIndex,
            model     = ctx.ModelId,
        });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /sessions
    // -------------------------------------------------------------------------

    private static async Task CmdSessionsAsync(bool jsonMode, CancellationToken cancellationToken)
    {
        var sessions = await ReplSessionSnapshot.ListAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No saved sessions found.[/]");
            return;
        }

        if (jsonMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## Saved Sessions ({sessions.Count})\n");
            foreach (var s in sessions)
            {
                var age   = DateTime.UtcNow - s.LastUpdatedAt;
                var label = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago"
                          : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago"
                          : $"{(int)age.TotalMinutes}m ago";
                var turns = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
                var title = (s.Title ?? s.AutoTitle) is { } shown ? $" — {shown}" : string.Empty;
                sb.AppendLine(
                    $"- **`{s.SessionId}`**{title} — {s.ModelId}, {turns}, {label} *({Path.GetFileName(s.Cwd)})*");
            }
            sb.AppendLine();
            sb.AppendLine("Resume a session with `/resume` if it's already loaded, or restart the panel and select the session.");
            ReplJsonBridge.Emit(new { type = "text", text = sb.ToString() });
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Saved sessions ({sessions.Count}):[/]");
        AnsiConsole.WriteLine();

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 2, 0))); // ID
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // model
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // turns
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // age
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // path
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 0, 0))); // title

        grid.AddRow(
            "[dim underline]ID[/]",
            "[dim underline]Model[/]",
            "[dim underline]Turns[/]",
            "[dim underline]Age[/]",
            "[dim underline]Path[/]",
            "[dim underline]Title[/]");

        foreach (var s in sessions)
        {
            var elapsed = DateTime.UtcNow - s.LastUpdatedAt;
            var age     = elapsed.TotalDays  >= 1 ? $"{(int)elapsed.TotalDays}d ago"
                        : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h ago"
                        :                           $"{(int)elapsed.TotalMinutes}m ago";
            var turns   = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
            var model   = s.ModelId.Length > 28 ? s.ModelId[..27] + "…" : s.ModelId;
            var cwd     = Path.GetFileName(s.Cwd);
            var shown   = s.Title ?? s.AutoTitle;
            var title   = shown is { Length: > 40 } t ? t[..39] + "…" : shown ?? string.Empty;

            grid.AddRow(
                $"[bold cyan]{Markup.Escape(s.SessionId)}[/]",
                $"[dim]{Markup.Escape(model)}[/]",
                $"[dim]{Markup.Escape(turns)}[/]",
                $"[dim]{Markup.Escape(age)}[/]",
                $"[dim]{Markup.Escape(cwd)}[/]",
                Markup.Escape(title));
        }

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  Resume with:[/] [bold]fuseraft repl --resume <id>[/]");
    }

    // -------------------------------------------------------------------------
    // /snapshot
    // -------------------------------------------------------------------------

    private static async Task CmdSnapshotAsync(ReplSessionContext ctx)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(FuseraftPaths.SystemTempRoot, $"repl-snapshot-{ctx.SessionId}-{timestamp}.json");
        Directory.CreateDirectory(FuseraftPaths.SystemTempRoot);

        var snapshot = new
        {
            session = new
            {
                sessionId              = ctx.SessionId,
                modelId                = ctx.ModelId,
                cwd                    = ctx.Cwd,
                eventsPath             = ctx.EventsPath,
                startedAt              = ctx.StartedAt,
                capturedAt             = DateTime.UtcNow,
                turnIndex              = ctx.TurnIndex,
                lastExtractedTurnIndex = ctx.LastExtractedTurnIndex,
                pendingSave            = ctx.PendingSave,
            },
            modes = new
            {
                jsonMode        = ctx.JsonMode,
                safeMode        = ctx.SafeMode,
                hitlMode        = ctx.HitlMode,
                adversarialMode = ctx.AdversarialMode,
                maxOutputTokens = ctx.MaxOutputTokens,
                temperature     = ctx.Temperature,
                topP            = ctx.TopP,
                seed            = ctx.Seed,
                verbose         = ctx.Verbose,
            },
            context = new
            {
                estimatedTokens       = ctx.EstimateTokens(),
                prevCtxEstimate       = ctx.PrevCtxEstimate,
                prevTurnTokenEstimate = ctx.PrevTurnTokenEstimate,
                turnTokenDeltas       = ctx.TurnTokenDeltas,
                contextWarningShown   = ctx.ContextWarningShown,
            },
            tools = new
            {
                disabledCategories     = ctx.DisabledCategories.ToList(),
                capabilityRestrictions = ctx.CapabilityRestrictions.ToDictionary(kv => kv.Key, kv => kv.Value),
                activeCount        = ctx.GetActiveTools().Count,
                categories         = ctx.ToolsByCategory.Select(kv => new
                {
                    category = kv.Key,
                    disabled = ctx.DisabledCategories.Contains(kv.Key),
                    count    = kv.Value.Count,
                    tools    = kv.Value.Select(t => t.Name).ToList(),
                }).ToList(),
            },
            plan = ctx.CurrentPlan is null && ctx.ExecutionQueue.Count == 0 && ctx.HaltedAt is null
                ? (object?)null
                : new
                {
                    currentPlan     = ctx.CurrentPlan,
                    executionQueue  = ctx.ExecutionQueue.Select(e => new { step = e.Step, total = e.Total }).ToArray(),
                    haltedAt        = ctx.HaltedAt is { } h ? new { step = h.Step, total = h.Total } : (object?)null,
                    haltedRemaining = ctx.HaltedRemaining.Select(e => new { step = e.Step, total = e.Total }).ToArray(),
                    haltedToolCalls = ctx.HaltedToolCalls,
                    recoveryHint    = ctx.RecoveryHint,
                },
            history = ctx.History.Select(ReplSerializedMessage.From).ToList(),
        };

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, opts));
        AnsiConsole.MarkupLine($"[green]Snapshot written:[/] {Markup.Escape(path)}");
    }
}
