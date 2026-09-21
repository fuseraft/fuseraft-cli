using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Shared construction helpers used by both ReplCommand (startup) and
/// ReplCommands (/provider setup). Decouples command handlers from the
/// bootstrap entry point.
/// </summary>
internal static class ReplFactory
{
    internal static ModelConfig BuildModelConfig(string modelId, UserConfig? userCfg, string? reasoningEffort = null) =>
        new()
        {
            ModelId         = modelId,
            Endpoint        = userCfg?.Endpoint ?? string.Empty,
            ApiKey          = userCfg?.ApiKey   ?? string.Empty,
            Provider        = userCfg?.Provider ?? string.Empty,
            ReasoningEffort = reasoningEffort,
        };

    /// <summary>Resolves the model for a REPL side-call (<c>memory.model</c>, <c>subagent.model</c>) that may not run on the main chat model.</summary>
    // An explicit endpoint makes the connection custom and reuses the main provider's key unless another is named.
    // Otherwise the ID prefix picks the provider, and an ID it doesn't recognize rides the main connection
    // instead of failing — a gateway serving models under names of its own.
    internal static ModelConfig ResolveOverrideModel(
        ChatClientFactory factory, ModelConfig main, string modelId,
        string? provider, string? endpoint, string? apiKeyEnvVar)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        provider    = Clean(provider);
        endpoint    = Clean(endpoint);
        apiKeyEnvVar = Clean(apiKeyEnvVar);

        var seed = new ModelConfig
        {
            ModelId      = modelId,
            Provider     = provider     ?? string.Empty,
            Endpoint     = endpoint     ?? string.Empty,
            ApiKeyEnvVar = apiKeyEnvVar ?? string.Empty,
        };

        if (endpoint is not null)
        {
            if (apiKeyEnvVar is null) seed = seed with { ApiKey = main.ApiKey, ApiKeyEnvVar = main.ApiKeyEnvVar };
            if (provider is null)     seed = seed with { Provider = main.Provider };
            return factory.Resolve(seed);
        }

        try
        {
            return factory.Resolve(seed);
        }
        catch (InvalidOperationException)
        {
            return main with
            {
                ModelId         = modelId,
                ReasoningEffort = null,
                Provider        = provider ?? main.Provider,
                ApiKeyEnvVar    = apiKeyEnvVar ?? main.ApiKeyEnvVar,
                ApiKey          = apiKeyEnvVar is null ? main.ApiKey : string.Empty,
            };
        }
    }

    // addFunctionInvocation controls whether the FunctionInvokingChatClient middleware is
    // attached. The actual tool list is supplied via ChatOptions at call time — this flag
    // only decides whether the invocation loop exists at all.
    //
    // adaptiveTrimTracker is required (not optional) whenever addFunctionInvocation is true:
    // without it, a provider ContextExceeded rejection has no way to signal ReplTurn that a
    // real /compact is needed afterward, which is exactly the gap that let REPL turns die
    // on context-overflow with no recovery path while `fuseraft run` self-healed (see
    // AgentMiddlewareBuilder.BuildMiddlewareChain and ReplTurn's post-turn ConsumeTrim check).
    internal static IChatClient BuildClient(
        ModelConfig config, ChatClientFactory factory, bool addFunctionInvocation,
        AdaptiveTrimTracker adaptiveTrimTracker, EventEmitter? emitter = null,
        int maxIterations = ReplTurn.ChatIterationLimit,
        IReadOnlyList<AIFunction>? tools = null,
        ReplLimits? limits = null)
    {
        var client = factory.Create(config);
        if (addFunctionInvocation)
        {
            var resolved = factory.Resolve(config);

            // Matches AgentFactory's fallback tier for agents with no explicit MaxContextTokens:
            // 0 disables pre-flight budget enforcement and proactive trim entirely (rare for
            // REPL, where users typically type a model ID with no Models-registry alias), but
            // the reactive adaptive-trim retry below fires unconditionally either way — it
            // reacts to the provider's own rejection rather than a configured estimate.
            var maxContextChars = resolved.MaxContextTokens > 0
                ? TokenEstimator.EstimateChars(resolved.MaxContextTokens)
                : 0;

            var agentConfig = new AgentConfig
            {
                Name = ReplAgentName,
                Model = resolved,
                MaxToolCallsPerTurn = maxIterations,
            };

            // Routes through the same context-trim/adaptive-retry middleware AgentFactory wraps
            // every orchestration agent with. chatOptions is null because the REPL's tool list
            // is supplied per-call via ChatOptions, not fixed at construction like an agent's —
            // toolSchemaChars is instead estimated from the caller's current tool set (whatever
            // is active at the moment this client is (re)built by /tools, /safe-mode, /model,
            // /mcp, etc.) so pre-flight budget checks and the inner_call_context/model_call
            // telemetry account for schema overhead instead of treating it as zero.
            var middleware = new AgentMiddlewareBuilder(
                logger: NullLogger.Instance, changeTracker: null, securityConfig: null,
                governanceKernel: null, adaptiveTrimTracker: adaptiveTrimTracker);
            var toolSchemaChars = AgentMiddlewareBuilder.EstimateToolSchemaChars(
                tools?.Cast<AITool>().ToList());

            // Mirrors AgentFactory's fallback tier (3/4) for orchestration agents with no
            // explicit per-agent/session override: fall back to the model's own context
            // window, or DefaultMaxInTurnChars when that isn't known either (e.g. a
            // manually typed model ID with no Models-registry entry, leaving maxContextChars
            // at 0). Without this, a single oversized tool result — a directory-wide search
            // that returns hundreds of KB, say — rides along unbounded for the rest of the
            // turn instead of being caught by TrimInTurnContext's Phase 2 (which truncates
            // even the most recent trimmable result once the turn is over budget).
            var maxInTurnChars = maxContextChars > 0 ? maxContextChars : DefaultMaxInTurnChars;

            client = middleware.BuildMiddlewareChain(
                chatClient: client, config: agentConfig, chatOptions: null,
                maxContextChars: maxContextChars, maxInTurnChars: maxInTurnChars, maxInTurnToolPairs: InTurnToolPairLimit,
                toolSchemaChars: toolSchemaChars, maxPayloadBytes: resolved.MaxPayloadBytes,
                hasHandoff: false, emitter: emitter);

            // ReplToolLoopGuard's soft repeated-call nudge only makes sense for free-form turns
            // (isStepRequest: false) — StepIterationLimit (5) already bounds a step turn tightly
            // enough that the extra mechanism isn't worth the complexity there. maxIterations
            // still carries that distinction here (ChatIterationLimit for free-form vs.
            // StepIterationLimit for steps — see ReplTurn.ChatIterationLimit's own comment on
            // why this parameter and that constant can't drift apart).
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? functionInvoker =
                maxIterations == ReplTurn.ChatIterationLimit
                    ? new ReplToolLoopGuard(limits).InvokeAsync
                    : null;
            client = AgentMiddlewareBuilder.BuildEventEmitMiddleware(
                client, agentConfig, skillsProvider: null, functionInvoker);
        }
        return client;
    }

    // Agent name used for AdaptiveTrimTracker.RecordTrim/ConsumeTrim correlation — the REPL
    // has exactly one agent identity, unlike orchestration's per-config agent names.
    internal const string ReplAgentName = "repl";

    // Matches AgentFactory.DefaultToolPairsWhenBudgeted — keeps at most this many
    // tool-call/result groups in full per inner LLM call within a single REPL turn.
    private const int InTurnToolPairLimit = 12;

    // Matches AgentFactory.DefaultMaxInTurnChars — conservative floor for the in-turn char
    // budget when the model's own context window isn't known (see BuildClient).
    private const int DefaultMaxInTurnChars = 200_000;

    internal static async Task<(UserConfig? Config, string? ApiKey, bool SelectedFromList)> RunSetupWizardAsync(
        string? currentModelId, UserConfig? currentCfg)
    {
        AnsiConsole.MarkupLine("[bold]Provider setup[/]");
        AnsiConsole.MarkupLine("[dim]Configure your provider and API key, then pick a model. " +
                               "Picking from a live model list saves immediately; a manually typed " +
                               "model ID is saved after your first successful reply.[/]");
        AnsiConsole.WriteLine();

        var defaultEndpoint = !string.IsNullOrEmpty(currentCfg?.Endpoint)
            ? currentCfg.Endpoint
            : "http://localhost:11434";
        var endpointInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[dim]Provider URL[/]")
                .DefaultValue(defaultEndpoint)
                .PromptStyle("white"));
        var endpoint = endpointInput.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            AnsiConsole.MarkupLine("[red]✗ Provider URL is required.[/]");
            return (null, null, false);
        }

        bool hasExistingKey = !string.IsNullOrEmpty(currentCfg?.ApiKey);
        var apiKeyPrompt = new TextPrompt<string>("[dim]API Key (leave blank for Ollama)[/]")
            .Secret('•')
            .AllowEmpty()
            .PromptStyle("white");
        if (hasExistingKey)
            apiKeyPrompt.DefaultValue(new string('•', 8));
        var apiKeyInput = AnsiConsole.Prompt(apiKeyPrompt);

        var apiKey = string.IsNullOrEmpty(apiKeyInput) || apiKeyInput == new string('•', 8)
            ? (currentCfg?.ApiKey ?? string.Empty)
            : apiKeyInput.Trim();

        AnsiConsole.WriteLine();

        string modelId;
        string provider;
        bool selectedFromList;

        var (modelIds, detectedProvider) = await TryFetchModelsAsync(endpoint, apiKey);
        if (modelIds is { Count: > 0 })
        {
            provider = detectedProvider;
            var defaultModel = !string.IsNullOrEmpty(currentCfg?.ModelId) && modelIds.Contains(currentCfg.ModelId)
                ? currentCfg.ModelId
                : modelIds[0];

            modelId = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[dim]Model[/] [dim]({modelIds.Count} available from {Markup.Escape(endpoint)})[/]")
                    .PageSize(15)
                    .MoreChoicesText("[dim](Move up/down to see more models)[/]")
                    .AddChoices(modelIds.OrderBy(m => m == defaultModel ? 0 : 1).ThenBy(m => m)));
            selectedFromList = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(apiKey) && !endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && !endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]✗ API key is required.[/]");
                return (null, null, false);
            }

            var fallbackDefault = !string.IsNullOrEmpty(currentCfg?.ModelId)
                ? currentCfg.ModelId
                : (currentModelId ?? "claude-sonnet-4-6");
            var modelIdInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[dim]Model ID[/]")
                    .DefaultValue(fallbackDefault)
                    .PromptStyle("white"));
            if (string.IsNullOrWhiteSpace(modelIdInput))
            {
                AnsiConsole.MarkupLine("[red]✗ Model ID is required.[/]");
                return (null, null, false);
            }
            modelId  = modelIdInput.Trim();
            provider = string.Empty; // let ChatClientFactory.Resolve auto-detect from the model ID
            selectedFromList = false;
        }

        AnsiConsole.WriteLine();

        return (ApplyWizardResult(currentCfg, modelId, endpoint, provider), apiKey, selectedFromList);
    }

    /// <summary>Applies the wizard's provider/model choice to the current config; the result is saved over <c>~/.fuseraft/config</c>, so every other section must survive.</summary>
    // The API-key env var is reset because it named the previous provider's key.
    internal static UserConfig ApplyWizardResult(UserConfig? current, string modelId, string endpoint, string provider)
    {
        var config = current?.Clone() ?? new UserConfig();
        config.ModelId      = modelId;
        config.Endpoint     = endpoint;
        config.Provider     = provider;
        config.ApiKeyEnvVar = string.Empty;
        return config;
    }

    // Tries the OpenAI-compatible /models endpoint first, then Ollama's /api/tags, then
    // Anthropic's native /v1/models — so typing Anthropic's bare endpoint (no /v1) here
    // still lands on the "anthropic" provider and gets prompt caching, not "openai".
    // Returns a null model list (and prints a warning) when nothing responds, so the
    // caller can fall back to manual model-ID entry.
    private static async Task<(List<string>? ModelIds, string Provider)> TryFetchModelsAsync(string endpoint, string apiKey)
    {
        try
        {
            return (await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama: false), "openai");
        }
        catch (ProviderConnectException ex)
        {
            // The host/port itself is unreachable — retrying a different path on the same
            // host would fail the same way, so don't bother and don't mask this error.
            ReportFetchFailure(endpoint, ex);
            return (null, "openai");
        }
        catch (Exception firstEx)
        {
            try
            {
                return (await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama: true), "ollama");
            }
            catch
            {
                try
                {
                    return (await ProviderModelsClient.FetchAnthropicAsync(endpoint, apiKey), "anthropic");
                }
                catch
                {
                    // Nothing worked — report the /models failure since that's the
                    // standard endpoint; the other two retries were just guesses.
                    ReportFetchFailure(endpoint, firstEx);
                    return (null, "openai");
                }
            }
        }
    }

    private static void ReportFetchFailure(string endpoint, Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ Could not fetch a model list from {Markup.Escape(endpoint)}:[/] [dim]{Markup.Escape(ex.Message)}[/]");
        AnsiConsole.MarkupLine("[dim]You can enter a model ID manually instead.[/]");
        AnsiConsole.WriteLine();
    }
}
