using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Spectre.Console;
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
    internal static IChatClient BuildClient(
        ModelConfig config, ChatClientFactory factory, bool addFunctionInvocation, int maxIterations = 20)
    {
        var client = factory.Create(config);
        if (addFunctionInvocation)
        {
            // Apply the same in-turn context filters that AgentFactory uses: deduplication of
            // superseded writes/reads/shells, intermediate-reasoning truncation, and a
            // sliding tool-pair window. These run on each inner LLM call within the
            // FunctionInvokingChatClient loop, keeping O(N²) token growth in check.
            client = client
                .AsBuilder()
                .Use(
                    getResponseFunc: async (messages, options, inner, ct) =>
                    {
                        messages = AgentContextCompactionFilters.DropSupersededWritePairs(messages);
                        messages = AgentContextCompactionFilters.DropSupersededObservationalPairs(messages);
                        messages = AgentContextCompactionFilters.CompressSupersededShellPairs(messages);
                        messages = AgentContextCompactionFilters.TruncateIntermediateAssistantReasoning(messages);
                        messages = await AgentContextCompactionFilters.KeepLastToolPairs(messages, InTurnToolPairLimit, ct);
                        return await inner.GetResponseAsync(messages, options, ct);
                    },
                    getStreamingResponseFunc: (messages, options, inner, ct) =>
                        StreamWithFiltersAsync(messages, options, inner, ct))
                .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
                .Build();
        }
        return client;

        async IAsyncEnumerable<ChatResponseUpdate> StreamWithFiltersAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IChatClient inner,
            [EnumeratorCancellation] CancellationToken ct)
        {
            messages = AgentContextCompactionFilters.DropSupersededWritePairs(messages);
            messages = AgentContextCompactionFilters.DropSupersededObservationalPairs(messages);
            messages = AgentContextCompactionFilters.CompressSupersededShellPairs(messages);
            messages = AgentContextCompactionFilters.TruncateIntermediateAssistantReasoning(messages);
            messages = await AgentContextCompactionFilters.KeepLastToolPairs(messages, InTurnToolPairLimit, ct);
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, ct))
                yield return update;
        }
    }

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
