using System.Text;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /context
    // -------------------------------------------------------------------------

    private static async Task CmdContextAsync(ReplSessionContext ctx)
    {
        static int EstMsg(ChatMessage m) => m.Contents.Sum(AgentContextCompactionFilters.EstimateContentChars) / 4;

        var active      = ctx.GetActiveTools();
        var sysTok      = ctx.History.Where(m => m.Role == ChatRole.System).Sum(EstMsg);
        var userTok     = ctx.History.Where(m => m.Role == ChatRole.User).Sum(EstMsg);
        var asstTok     = ctx.History.Where(m => m.Role == ChatRole.Assistant).Sum(EstMsg);
        var toolResTok  = ctx.History.Where(m => m.Role == ChatRole.Tool).Sum(EstMsg);
        var toolTok     = active.Sum(t => t.JsonSchema.GetRawText().Length / 4);
        // estTotal drives the per-category breakdown below (so its rows always sum to ~100%).
        // The headline number instead prefers the real provider-reported size of the most
        // recently completed turn's opening request, when available — falling back to the
        // char-based estimate for a fresh session or a provider that never reports usage.
        var estTotal    = sysTok + userTok + asstTok + toolResTok + toolTok;
        var actualTotal = ctx.LastActualContextTokens;
        var isActual    = actualTotal.HasValue;
        var total       = actualTotal ?? estTotal;
        var pct      = (double)total / ctx.ContextTokenBudget * 100;

        if (ctx.JsonMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Context Usage\n");
            var deltaNote = ctx.PrevCtxEstimate > 0
                ? (total - ctx.PrevCtxEstimate is var d and >= 0
                    ? $" *(+{d:N0} since last check)*"
                    : $" *({total - ctx.PrevCtxEstimate:N0} since last check)*")
                : string.Empty;
            sb.AppendLine($"**~{total:N0} / {ctx.ContextTokenBudget:N0} tokens** " +
                $"({(isActual ? "actual, as of last turn" : "estimated")}) — {pct:F1}%{deltaNote}");
            sb.AppendLine();
            sb.AppendLine($"**{ctx.TurnIndex} turn{(ctx.TurnIndex != 1 ? "s" : "")}** " +
                $"({ctx.History.Count} messages — " +
                $"system: {ctx.History.Count(m => m.Role == ChatRole.System)}, " +
                $"user: {ctx.History.Count(m => m.Role == ChatRole.User)}, " +
                $"assistant: {ctx.History.Count(m => m.Role == ChatRole.Assistant)})");
            if (ctx.CumulativeInputTokens > 0 || ctx.CumulativeOutputTokens > 0)
                sb.AppendLine($"**Session usage (actual):** {ctx.CumulativeInputTokens:N0} in / " +
                    $"{ctx.CumulativeOutputTokens:N0} out / {ctx.CumulativeInputTokens + ctx.CumulativeOutputTokens:N0} total tok");
            sb.AppendLine();
            sb.AppendLine("**Breakdown (estimated composition)**");
            if (sysTok > 0)
                sb.AppendLine($"- System prompt: {sysTok:N0} tok ({(double)sysTok / estTotal * 100:F1}%)");
            if (active.Count > 0)
                sb.AppendLine($"- Tools ({active.Count}): {toolTok:N0} tok ({(double)toolTok / estTotal * 100:F1}%) *(per request)*");
            sb.AppendLine($"- User messages: {userTok:N0} tok ({(double)userTok / estTotal * 100:F1}%)");
            sb.AppendLine($"- Assistant messages: {asstTok:N0} tok ({(double)asstTok / estTotal * 100:F1}%)");
            if (toolResTok > 0)
                sb.AppendLine($"- Tool results: {toolResTok:N0} tok ({(double)toolResTok / estTotal * 100:F1}%)");
            if (ctx.TurnTokenDeltas.Count >= 1)
            {
                var avg = (int)Math.Round(ctx.TurnTokenDeltas.Average());
                if (avg > 0)
                {
                    var proj = (ctx.ContextTokenBudget - total) / avg;
                    sb.AppendLine();
                    sb.AppendLine($"*~{proj:N0} turns remaining (avg +{avg:N0} tok/turn)*");
                }
            }
            Console.Write(sb.ToString());
            ctx.PrevCtxEstimate = total;
            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
            {
                command = "/context",
                estimated_tokens = estTotal,
                actual_context_tokens = actualTotal,
                displayed_tokens = total,
                is_actual = isActual,
                token_budget = ctx.ContextTokenBudget,
                turns = ctx.TurnIndex,
                breakdown = new { system = sysTok, tools = toolTok, user = userTok, assistant = asstTok, tool_results = toolResTok },
                cumulative_input_tokens = ctx.CumulativeInputTokens,
                cumulative_output_tokens = ctx.CumulativeOutputTokens,
            });
            return;
        }

        var bar      = new string('█', (int)(pct / 5)).PadRight(20, '░');
        var deltaStr = ctx.PrevCtxEstimate > 0
            ? (total - ctx.PrevCtxEstimate is var d2 and >= 0
                ? $"  [dim](+{d2:N0} since last check)[/]"
                : $"  [dim]({total - ctx.PrevCtxEstimate:N0} since last check)[/]")
            : string.Empty;

        var totalLabel = isActual ? "Tokens (actual):" : "Tokens (est.):";
        AnsiConsole.MarkupLine(
            $"  [dim]{totalLabel}[/] [bold]{total:N0}[/] / {ctx.ContextTokenBudget:N0}  " +
            $"[{(pct >= 90 ? "red" : pct >= 70 ? "yellow" : "green")}]{Markup.Escape(bar)}[/]  " +
            $"[dim]{pct:F1}%[/]{deltaStr}" +
            (isActual ? "  [dim](as of last turn's request)[/]" : string.Empty));
        AnsiConsole.MarkupLine(
            $"  [dim]Budget:[/]       [bold]{ctx.ContextTokenBudget:N0}[/]  [dim](context window ceiling)[/]");
        AnsiConsole.MarkupLine(
            $"  [dim]Turns:[/]        [bold]{ctx.TurnIndex}[/]  " +
            $"[dim](messages: {ctx.History.Count} — " +
            $"system: {ctx.History.Count(m => m.Role == ChatRole.System)}, " +
            $"user: {ctx.History.Count(m => m.Role == ChatRole.User)}, " +
            $"assistant: {ctx.History.Count(m => m.Role == ChatRole.Assistant)})[/]");
        if (ctx.CumulativeInputTokens > 0 || ctx.CumulativeOutputTokens > 0)
            AnsiConsole.MarkupLine(
                $"  [dim]Session usage:[/]   [bold]{ctx.CumulativeInputTokens:N0}[/] in / " +
                $"[bold]{ctx.CumulativeOutputTokens:N0}[/] out  " +
                $"[dim]({ctx.CumulativeInputTokens + ctx.CumulativeOutputTokens:N0} total tok, actual)[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [dim]Breakdown (estimated composition):[/]");
        PrintContextRow("system prompt",  sysTok,  estTotal);
        if (active.Count > 0)
            PrintContextRow($"tools ({active.Count})", toolTok, estTotal, "(per req.)");
        PrintContextRow("user messages",  userTok, estTotal);
        PrintContextRow("assistant msgs", asstTok, estTotal);
        if (toolResTok > 0)
            PrintContextRow("tool results",  toolResTok, estTotal);

        if (ctx.TurnTokenDeltas.Count >= 1)
        {
            var avg = (int)Math.Round(ctx.TurnTokenDeltas.Average());
            if (avg > 0)
            {
                var proj = (ctx.ContextTokenBudget - total) / avg;
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [dim]Projected:[/]    ~{proj:N0} turns remaining  [dim](avg +{avg:N0} tok/turn)[/]");
            }
        }

        ctx.PrevCtxEstimate = total;
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
        {
            command = "/context",
            estimated_tokens = estTotal,
            actual_context_tokens = actualTotal,
            displayed_tokens = total,
            is_actual = isActual,
            token_budget = ctx.ContextTokenBudget,
            turns = ctx.TurnIndex,
            breakdown = new { system = sysTok, tools = toolTok, user = userTok, assistant = asstTok },
            cumulative_input_tokens = ctx.CumulativeInputTokens,
            cumulative_output_tokens = ctx.CumulativeOutputTokens,
        });
    }

    // -------------------------------------------------------------------------
    // /max-tokens
    // -------------------------------------------------------------------------

    private static CommandResult CmdMaxTokens(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            AnsiConsole.MarkupLine(ctx.MaxOutputTokens > 0
                ? $"[dim]Max output tokens:[/] [bold]{ctx.MaxOutputTokens:N0}[/]"
                : "[dim]Max output tokens:[/] provider default");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/max-tokens <n>[/] [dim]to set, or[/] [bold]/max-tokens reset[/] [dim]to restore the provider default.[/]");
            return CommandResult.Continue;
        }

        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            ctx.MaxOutputTokens = 0;
            ctx.ChatOptions = ctx.BuildChatOptions();
            AnsiConsole.MarkupLine("[dim]Max output tokens reset to provider default.[/]");
            return CommandResult.Continue;
        }

        if (!int.TryParse(arg, out var n) || n <= 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Invalid value:[/] {Markup.Escape(arg)}  [dim](must be a positive integer)[/]");
            return CommandResult.Continue;
        }

        ctx.MaxOutputTokens = n;
        ctx.ChatOptions = ctx.BuildChatOptions();
        AnsiConsole.MarkupLine($"[dim]Max output tokens set to[/] [bold]{n:N0}[/][dim].[/]");
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /provider
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdProviderAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            var epDisplay  = string.IsNullOrEmpty(ctx.ModelConfig.Endpoint) ? "(auto-detected)" : ctx.ModelConfig.Endpoint;
            var keyDisplay = string.IsNullOrEmpty(ctx.ModelConfig.ApiKey)
                ? "(from environment)"
                : $"••••••••  [[{Markup.Escape(ctx.KeyStore.StoreName)}]]";
            AnsiConsole.MarkupLine($"  [dim]Model:[/]    [bold]{Markup.Escape(ctx.ModelId)}[/]");
            AnsiConsole.MarkupLine($"  [dim]Endpoint:[/] {Markup.Escape(epDisplay)}");
            AnsiConsole.MarkupLine($"  [dim]API Key:[/]  {keyDisplay}");
            AnsiConsole.MarkupLine($"  [dim]Config:[/]   {Markup.Escape(UserConfigStore.ConfigPath)}");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/provider setup[/] [dim]to reconfigure.[/]");
            return CommandResult.Continue;
        }

        if (!arg.Equals("setup", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /provider subcommand:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /provider          — show current settings[/]");
            AnsiConsole.MarkupLine("[dim]       /provider setup     — reconfigure provider, model, and API key[/]");
            return CommandResult.Continue;
        }

        if (ctx.JsonMode)
        {
            Console.WriteLine("Provider setup requires an interactive terminal and is not available in the VS Code panel.\n\nRun **`fuseraft repl`** in a terminal to reconfigure your provider, model, and API key.");
            return CommandResult.Continue;
        }

        AnsiConsole.WriteLine();
        var (newCfg, newKey, _) = await ReplFactory.RunSetupWizardAsync(ctx.ModelId, ctx.UserCfg);
        if (newCfg is null || newKey is null) return CommandResult.Continue;

        ctx.KeyStored = string.IsNullOrEmpty(newKey) || await KeyStorePersistence.TryStoreAsync(ctx.KeyStore, newKey);
        newCfg.ApiKey    = newKey;
        ctx.UserCfg      = newCfg;
        ctx.ModelId      = newCfg.ModelId;
        ctx.ModelConfig  = ReplFactory.BuildModelConfig(ctx.ModelId, ctx.UserCfg);
        try
        {
            var hasTools       = ctx.GetActiveTools().Count > 0;
            ctx.Client         = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools);
            ctx.StepClient     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not create chat client:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var sys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        ctx.History.Clear();
        if (sys is not null) ctx.History.Add(sys);
        ctx.TurnIndex    = 0;
        ctx.PendingSave  = false;
        UserConfigStore.Save(ctx.UserCfg);
        AnsiConsole.MarkupLine($"[dim]Settings saved to[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
        if (ctx.KeyStored)
            AnsiConsole.MarkupLine($"[dim]API key stored in[/] [bold]{Markup.Escape(ctx.KeyStore.StoreName)}[/]");
        AnsiConsole.MarkupLine($"[dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]  [dim](history cleared)[/]");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/provider setup", model = ctx.ModelId });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /model
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdModelAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var effortDisplay = ctx.ModelConfig.ReasoningEffort is { } e
                ? $"  [dim]Reasoning:[/] [bold]{Markup.Escape(e)}[/]" : string.Empty;
            AnsiConsole.MarkupLine($"  [dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]{effortDisplay}");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/model <id> [[effort]][/] [dim]to switch models. Effort: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        // Optional second token is reasoning effort: /model grok-4.3 low
        var parts      = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var newModelId = parts[0];
        var newEffort  = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        if (newEffort is not null and not ("none" or "low" or "medium" or "high"))
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid reasoning effort '{Markup.Escape(newEffort)}'.[/] [dim]Valid values: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        if (newModelId.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase)
            && newEffort == ctx.ModelConfig.ReasoningEffort)
        {
            AnsiConsole.MarkupLine($"[dim]Already using[/] [bold]{Markup.Escape(ctx.ModelId)}[/][dim].[/]");
            return CommandResult.Continue;
        }

        var newConfig = ReplFactory.BuildModelConfig(newModelId, ctx.UserCfg, newEffort);
        var hasTools  = ctx.GetActiveTools().Count > 0;
        IChatClient newClient;
        try
        {
            newClient = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Could not create client for {Markup.Escape(newModelId)}:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var prevModel   = ctx.ModelId;
        ctx.ModelId     = newModelId;
        ctx.ModelConfig = newConfig;
        ctx.Client      = newClient;
        ctx.StepClient  = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);

        // Keep the system message identity line current with the new model.
        var sysIdx = ctx.History.FindIndex(m => m.Role == ChatRole.System);
        if (sysIdx >= 0 && ctx.History[sysIdx].Text is { } sysText)
        {
            var updated = sysText.Replace(
                $"running on {prevModel}", $"running on {newModelId}",
                StringComparison.OrdinalIgnoreCase);
            ctx.History[sysIdx] = new ChatMessage(ChatRole.System, updated);
        }

        var effortSuffix = newEffort is not null ? $" [dim](reasoning: {Markup.Escape(newEffort)})[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"[dim]Model:[/] [bold]{Markup.Escape(prevModel)}[/] [dim]→[/] [bold]{Markup.Escape(newModelId)}[/]{effortSuffix}  " +
            $"[dim](history preserved)[/]");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/model", model = newModelId, prev = prevModel, reasoning_effort = newEffort });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /reasoning
    // -------------------------------------------------------------------------

    private static readonly string[] ValidReasoningEfforts = ["none", "low", "medium", "high"];

    private static async Task<CommandResult> CmdReasoningAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var current = ctx.ModelConfig.ReasoningEffort ?? "(not set)";
            AnsiConsole.MarkupLine($"  [dim]Reasoning effort:[/] [bold]{Markup.Escape(current)}[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/reasoning <none|low|medium|high>[/] [dim]to change.[/]");
            return CommandResult.Continue;
        }

        var effort = arg.Trim().ToLowerInvariant();
        if (!ValidReasoningEfforts.Contains(effort))
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid value '{Markup.Escape(effort)}'.[/] [dim]Valid values: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        var prev = ctx.ModelConfig.ReasoningEffort;
        if (effort == prev)
        {
            AnsiConsole.MarkupLine($"[dim]Reasoning effort already set to[/] [bold]{Markup.Escape(effort)}[/][dim].[/]");
            return CommandResult.Continue;
        }

        ctx.ModelConfig = ctx.ModelConfig with { ReasoningEffort = effort };
        var hasTools = ctx.GetActiveTools().Count > 0;
        try
        {
            ctx.Client     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools);
            ctx.StepClient = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
        }
        catch (Exception ex)
        {
            ctx.ModelConfig = ctx.ModelConfig with { ReasoningEffort = prev };
            AnsiConsole.MarkupLine($"[red]✗ Could not apply reasoning effort:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var prevDisplay = prev ?? "(none)";
        AnsiConsole.MarkupLine($"[dim]Reasoning:[/] [bold]{Markup.Escape(prevDisplay)}[/] [dim]→[/] [bold]{Markup.Escape(effort)}[/]");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/reasoning", reasoning_effort = effort, prev = prevDisplay, model = ctx.ModelId });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /models
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdModelsAsync(ReplSessionContext ctx, CancellationToken cancellationToken)
    {
        fuseraft.Core.Models.Config.ModelConfig resolved;
        try
        {
            resolved = ctx.Factory.Resolve(ctx.ModelConfig);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not resolve provider config:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var endpoint = resolved.Endpoint.TrimEnd('/');
        var apiKey = !string.IsNullOrEmpty(resolved.ApiKey)
            ? resolved.ApiKey
            : string.IsNullOrEmpty(resolved.ApiKeyEnvVar)
                ? string.Empty
                : Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar) ?? string.Empty;

        bool isOllama = resolved.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);

        List<string> modelIds;
        try
        {
            modelIds = await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return CommandResult.Continue;
        }

        if (ctx.JsonMode)
        {
            Console.WriteLine($"## Available Models ({modelIds.Count})\n");
            foreach (var m in modelIds)
                Console.WriteLine($"- `{m}`{(m.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase) ? " ← current" : "")}");
            return CommandResult.Continue;
        }

        AnsiConsole.MarkupLine($"  [dim]Available models from[/] [bold]{Markup.Escape(endpoint)}[/] [dim]({modelIds.Count})[/]");
        AnsiConsole.WriteLine();
        foreach (var m in modelIds)
        {
            if (m.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase))
                AnsiConsole.MarkupLine($"  [bold green]{Markup.Escape(m)}[/] [dim]← current[/]");
            else
                AnsiConsole.MarkupLine($"  {Markup.Escape(m)}");
        }

        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Display utility
    // -------------------------------------------------------------------------

    private static void PrintContextRow(string label, int tokens, int total, string? note = null)
    {
        var pct         = total > 0 ? (double)tokens / total * 100.0 : 0.0;
        var bar         = new string('█', (int)(pct / 5)).PadRight(20, '░');
        var paddedLabel = label.PadRight(15);
        var suffix      = note is not null ? $" [dim]{Markup.Escape(note)}[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"    [dim]{Markup.Escape(paddedLabel)}[/] [bold]{tokens,7:N0}[/] [dim]tok  {pct,5:F1}%  {bar}[/]{suffix}");
    }
}
