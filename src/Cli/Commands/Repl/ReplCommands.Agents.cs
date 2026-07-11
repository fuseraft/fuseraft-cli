using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /assist
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdAssistAsync(
        ReplSessionContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (ctx.TurnIndex == 0)
        {
            AnsiConsole.MarkupLine("[dim]No conversation yet — nothing to diagnose.[/]");
            return CommandResult.Continue;
        }

        // Spinner pollutes the captured JSON-mode output — skip it entirely there.
        var spinCts  = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask = spinCts is not null
            ? ReplConsole.RunSpinnerAsync("diagnosing…", spinCts.Token)
            : Task.CompletedTask;
        try
        {
            var correction = await ctx.SubAgent.DiagnoseAsync(ctx.History, cancellationToken);
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplConsole.ClearSpinnerLine(); }

            if (correction is null)
            {
                AnsiConsole.MarkupLine("[dim]Diagnosis returned no output.[/]");
                return CommandResult.Continue;
            }

            // In JSON mode the correction text is injected silently; the webview will see the
            // AI's streamed response as a fresh assistant bubble via the SendInput path.
            if (!ctx.JsonMode)
            {
                AnsiConsole.MarkupLine("[dim]assist →[/]");
                AnsiConsole.WriteLine(correction);
                AnsiConsole.WriteLine();
            }
            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/assist" });
            return CommandResult.Send(correction);
        }
        catch (OperationCanceledException)
        {
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplConsole.ClearSpinnerLine(); }
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            return CommandResult.Continue;
        }
        catch (Exception ex)
        {
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplConsole.ClearSpinnerLine(); }
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return CommandResult.Continue;
        }
    }

    // -------------------------------------------------------------------------
    // /explore
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdExploreAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /explore <query>[/]");
            return CommandResult.Continue;
        }

        var spinCts       = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask      = spinCts is not null
            ? ReplConsole.RunSpinnerAsync("exploring…", spinCts.Token)
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
            var (_, inputTok, outputTok) = await ctx.SubAgent.ExploreStreamingAsync(arg,
                async chunk =>
                {
                    if (!headerPrinted)
                    {
                        headerPrinted = true;
                        await StopSpinner();
                        if (!ctx.JsonMode) AnsiConsole.MarkupLine("[dim]assistant:[/]");
                    }
                    await ReplConsole.WriteChunkSmoothAsync(chunk, cancellationToken);
                },
                cancellationToken: cancellationToken);
            ctx.CumulativeInputTokens  += inputTok  ?? 0;
            ctx.CumulativeOutputTokens += outputTok ?? 0;

            await StopSpinner();
            if (headerPrinted) { if (!ctx.JsonMode) AnsiConsole.WriteLine(); }
            else AnsiConsole.MarkupLine("[dim](no output)[/]");
            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/explore", query = arg });
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

    // -------------------------------------------------------------------------
    // /locate
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdLocateAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /locate <symbol>[/]");
            return CommandResult.Continue;
        }

        var spinCts      = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask     = spinCts is not null
            ? ReplConsole.RunSpinnerAsync("locating…", spinCts.Token)
            : Task.CompletedTask;
        bool spinStopped = false;
        bool gotOutput   = false;

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
            var (_, inputTok, outputTok) = await ctx.SubAgent.LocateStreamingAsync(arg,
                async chunk =>
                {
                    if (!gotOutput)
                    {
                        gotOutput = true;
                        await StopSpinner();
                    }
                    await ReplConsole.WriteChunkSmoothAsync(chunk, cancellationToken);
                },
                cancellationToken: cancellationToken);
            ctx.CumulativeInputTokens  += inputTok  ?? 0;
            ctx.CumulativeOutputTokens += outputTok ?? 0;

            await StopSpinner();
            if (gotOutput) { if (!ctx.JsonMode) AnsiConsole.WriteLine(); }
            else AnsiConsole.MarkupLine("[dim](not found)[/]");
            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/locate", target = arg });
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
