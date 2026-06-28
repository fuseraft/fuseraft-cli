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
            recoveryHint:    ctx.RecoveryHint);

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
                Console.WriteLine(
                    $"## Switched to Fork\n\n" +
                    $"Previous session: **`{prevId}`** (saved)\n\n" +
                    $"Now running as: **`{forkId}`**");
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
                Console.WriteLine(
                    $"## Session Forked\n\n" +
                    $"New session ID: **`{forkId}`**\n\n" +
                    $"Resume with: `fuseraft repl --resume {forkId}`\n\n" +
                    $"Or use `/fork switch` to branch and continue as the fork immediately.");
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

        var prevId    = ctx.SessionId;
        var prevModel = ctx.ModelId;

        // Switch model when the target session used a different one.
        if (!snapshot.ModelId.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var hasTools  = ctx.GetActiveTools().Count > 0;
            var newConfig = ReplFactory.BuildModelConfig(snapshot.ModelId, ctx.UserCfg);
            try
            {
                var newClient     = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools);
                var newStepClient = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
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
        ctx.LastExtractedTurnIndex = -1;
        ctx.ContextWarningShown    = false;
        ctx.ResetPlanState();

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
            Console.WriteLine(
                $"## Switched Session\n\n" +
                $"Now running as: **`{snapshot.SessionId}`** (was `{prevId}`)\n\n" +
                $"Model: {ctx.ModelId} · {snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
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
            Console.WriteLine($"## Saved Sessions ({sessions.Count})\n");
            foreach (var s in sessions)
            {
                var age   = DateTime.UtcNow - s.LastUpdatedAt;
                var label = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago"
                          : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago"
                          : $"{(int)age.TotalMinutes}m ago";
                var turns = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
                Console.WriteLine(
                    $"- **`{s.SessionId}`** — {s.ModelId}, {turns}, {label} *({Path.GetFileName(s.Cwd)})*");
            }
            Console.WriteLine();
            Console.WriteLine("Resume a session with `/resume` if it's already loaded, or restart the panel and select the session.");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Saved sessions ({sessions.Count}):[/]");
        AnsiConsole.WriteLine();

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 2, 0))); // ID
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // model
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // turns
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // age
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 0, 0))); // label

        foreach (var s in sessions)
        {
            var elapsed = DateTime.UtcNow - s.LastUpdatedAt;
            var age     = elapsed.TotalDays  >= 1 ? $"{(int)elapsed.TotalDays}d ago"
                        : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h ago"
                        :                           $"{(int)elapsed.TotalMinutes}m ago";
            var turns   = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
            var model   = s.ModelId.Length > 22 ? s.ModelId[..21] + "…" : s.ModelId;
            var cwd     = Path.GetFileName(s.Cwd);

            grid.AddRow(
                $"[bold cyan]{Markup.Escape(s.SessionId)}[/]",
                $"[dim]{Markup.Escape(model)}[/]",
                $"[dim]{Markup.Escape(turns)}[/]",
                $"[dim]{Markup.Escape(age)}[/]",
                $"[dim]{Markup.Escape(cwd)}[/]");
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
                adversarialMode = ctx.AdversarialMode,
                maxOutputTokens = ctx.MaxOutputTokens,
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
                disabledCategories = ctx.DisabledCategories.ToList(),
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
