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
        int maxIterations = ReplTurn.ChatIterationLimit)
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
            // is supplied per-call via ChatOptions, not fixed at construction like an agent's.
            var middleware = new AgentMiddlewareBuilder(
                logger: NullLogger.Instance, changeTracker: null, securityConfig: null,
                governanceKernel: null, adaptiveTrimTracker: adaptiveTrimTracker);

            client = middleware.BuildMiddlewareChain(
                chatClient: client, config: agentConfig, chatOptions: null,
                maxContextChars: maxContextChars, maxInTurnChars: 0, maxInTurnToolPairs: InTurnToolPairLimit,
                toolSchemaChars: 0, maxPayloadBytes: resolved.MaxPayloadBytes,
                hasHandoff: false, emitter: emitter);

            client = AgentMiddlewareBuilder.BuildEventEmitMiddleware(client, agentConfig, skillsProvider: null);
        }
        return client;
    }

    // Agent name used for AdaptiveTrimTracker.RecordTrim/ConsumeTrim correlation — the REPL
    // has exactly one agent identity, unlike orchestration's per-config agent names.
    internal const string ReplAgentName = "repl";

    // Matches AgentFactory.DefaultToolPairsWhenBudgeted — keeps at most this many
    // tool-call/result groups in full per inner LLM call within a single REPL turn.
    private const int InTurnToolPairLimit = 12;

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

        var (modelIds, isOllama) = await TryFetchModelsAsync(endpoint, apiKey);
        if (modelIds is { Count: > 0 })
        {
            provider = isOllama ? "ollama" : "openai";
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

        var config = new UserConfig
        {
            ModelId  = modelId,
            Endpoint = endpoint,
            Provider = provider,
        };
        return (config, apiKey, selectedFromList);
    }

    // Tries the OpenAI-compatible /models endpoint first, then falls back to Ollama's
    // /api/tags. Returns a null model list (and prints a warning) when neither responds,
    // so the caller can fall back to manual model-ID entry.
    private static async Task<(List<string>? ModelIds, bool IsOllama)> TryFetchModelsAsync(string endpoint, string apiKey)
    {
        try
        {
            return (await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama: false), false);
        }
        catch (ProviderConnectException ex)
        {
            // The host/port itself is unreachable — retrying a different path on the same
            // host would fail the same way, so don't bother and don't mask this error.
            ReportFetchFailure(endpoint, ex);
            return (null, false);
        }
        catch (Exception firstEx)
        {
            try
            {
                return (await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama: true), true);
            }
            catch
            {
                // Neither shape worked — report the /models failure since that's the
                // standard endpoint; the /api/tags retry was just a guess.
                ReportFetchFailure(endpoint, firstEx);
                return (null, false);
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
