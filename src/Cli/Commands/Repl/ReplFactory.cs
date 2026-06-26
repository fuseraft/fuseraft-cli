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
                        messages = AgentFactory.DropSupersededWritePairs(messages);
                        messages = AgentFactory.DropSupersededObservationalPairs(messages);
                        messages = AgentFactory.CompressSupersededShellPairs(messages);
                        messages = AgentFactory.TruncateIntermediateAssistantReasoning(messages);
                        messages = await AgentFactory.KeepLastToolPairs(messages, InTurnToolPairLimit, ct);
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
            messages = AgentFactory.DropSupersededWritePairs(messages);
            messages = AgentFactory.DropSupersededObservationalPairs(messages);
            messages = AgentFactory.CompressSupersededShellPairs(messages);
            messages = AgentFactory.TruncateIntermediateAssistantReasoning(messages);
            messages = await AgentFactory.KeepLastToolPairs(messages, InTurnToolPairLimit, ct);
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, ct))
                yield return update;
        }
    }

    // Matches AgentFactory.DefaultToolPairsWhenBudgeted — keeps at most this many
    // tool-call/result groups in full per inner LLM call within a single REPL turn.
    private const int InTurnToolPairLimit = 12;

    internal static (UserConfig? Config, string? ApiKey) RunSetupWizard(
        string? currentModelId, UserConfig? currentCfg)
    {
        AnsiConsole.MarkupLine("[bold]Provider setup[/]");
        AnsiConsole.MarkupLine("[dim]Configure your default model and API key. " +
                               "Settings will be saved after the first successful reply.[/]");
        AnsiConsole.WriteLine();

        var defaultModel    = !string.IsNullOrEmpty(currentCfg?.ModelId) ? currentCfg!.ModelId : (currentModelId ?? "claude-sonnet-4-6");
        var defaultEndpoint = currentCfg?.Endpoint ?? string.Empty;

        if (string.IsNullOrEmpty(defaultEndpoint))
        {
            try
            {
                using var temp = new ChatClientFactory();
                defaultEndpoint = temp.Resolve(new ModelConfig { ModelId = defaultModel }).Endpoint;
            }
            catch { }
        }

        var modelIdInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[dim]Model ID[/]")
                .DefaultValue(defaultModel)
                .PromptStyle("white"));

        if (string.IsNullOrWhiteSpace(modelIdInput))
        {
            AnsiConsole.MarkupLine("[red]✗ Model ID is required.[/]");
            return (null, null);
        }

        if (!modelIdInput.Equals(defaultModel, StringComparison.OrdinalIgnoreCase))
        {
            defaultEndpoint = string.Empty;
            try
            {
                using var temp = new ChatClientFactory();
                defaultEndpoint = temp.Resolve(new ModelConfig { ModelId = modelIdInput.Trim() }).Endpoint;
            }
            catch { }
        }

        var endpointPrompt = new TextPrompt<string>("[dim]Provider URL[/]")
            .AllowEmpty()
            .PromptStyle("white");
        if (!string.IsNullOrEmpty(defaultEndpoint))
            endpointPrompt.DefaultValue(defaultEndpoint);
        var endpointInput = AnsiConsole.Prompt(endpointPrompt);

        bool hasExistingKey = !string.IsNullOrEmpty(currentCfg?.ApiKey);
        var apiKeyPrompt = new TextPrompt<string>("[dim]API Key[/]")
            .Secret('•')
            .AllowEmpty()
            .PromptStyle("white");
        if (hasExistingKey)
            apiKeyPrompt.DefaultValue(new string('•', 8));
        var apiKeyInput = AnsiConsole.Prompt(apiKeyPrompt);

        var apiKey = string.IsNullOrEmpty(apiKeyInput) || apiKeyInput == new string('•', 8)
            ? (currentCfg?.ApiKey ?? string.Empty)
            : apiKeyInput;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]✗ API key is required.[/]");
            return (null, null);
        }

        AnsiConsole.WriteLine();

        var config = new UserConfig
        {
            ModelId  = modelIdInput.Trim(),
            Endpoint = string.IsNullOrWhiteSpace(endpointInput) ? defaultEndpoint : endpointInput.Trim(),
        };
        return (config, apiKey.Trim());
    }
}
