using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Wraps <see cref="AnthropicClient"/>'s native Messages API as an <see cref="IChatClient"/>.
///
/// <para>
/// Anthropic's OpenAI-compatible endpoint (used by every other <c>claude-*</c> code path before
/// this class existed) does not support prompt caching at all — Anthropic's own docs say so
/// explicitly. This class talks to the native <c>/v1/messages</c> API instead so Claude requests
/// get real <c>cache_control</c> breakpoints.
/// </para>
///
/// <para>
/// <see cref="Anthropic.SDK"/>'s built-in <c>IChatClient</c> adapter (<c>MessagesEndpoint</c>
/// itself) only caches the static prefix — the last system-prompt block and the last tool
/// definition — via <see cref="PromptCacheType.AutomaticToolsAndSystem"/>. For an agentic CLI
/// like fuseraft, the conversation history (file contents, command output, prior turns) is
/// usually the larger and faster-growing share of the prompt, so this class adds one more
/// breakpoint on the last content block of the last message before every call. Anthropic caches
/// by prefix, so that single marker covers system + tools + the entire history built so far —
/// the same "one breakpoint on the newest turn" strategy Cline and Claude Code use. Anthropic
/// allows up to 4 breakpoints per request; this uses 3 (system, tools, tail).
/// </para>
/// </summary>
internal sealed class AnthropicPromptCachingChatClient(AnthropicClient client) : IChatClient
{
    private ChatClientMetadata? _metadata;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var parameters = BuildParameters(messages, options);
        MessageResponse response = await client.Messages.GetClaudeMessageAsync(parameters, cancellationToken);

        ChatMessage message = new(ChatRole.Assistant, ChatClientHelper.ProcessResponseContent(response));

        return new(message)
        {
            ResponseId = response.Id,
            FinishReason = response.StopReason switch
            {
                "max_tokens" => ChatFinishReason.Length,
                _ => ChatFinishReason.Stop,
            },
            ModelId = response.Model,
            RawRepresentation = response,
            Usage = response.Usage is { } usage ? ChatClientHelper.CreateUsageDetails(usage) : null,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = BuildParameters(messages, options);
        var thinking = string.Empty;

        await foreach (MessageResponse response in client.Messages.StreamClaudeMessageAsync(parameters, cancellationToken))
        {
            var update = new ChatResponseUpdate
            {
                ResponseId = response.Id,
                ModelId = response.Model,
                RawRepresentation = response,
                Role = ChatRole.Assistant,
            };

            if (!string.IsNullOrEmpty(response.ContentBlock?.Data))
                update.Contents.Add(new TextReasoningContent(null) { ProtectedData = response.ContentBlock.Data });

            if (response.StreamStartMessage?.Usage is { } startStreamMessageUsage)
                update.Contents.Add(new UsageContent(ChatClientHelper.CreateUsageDetails(startStreamMessageUsage)));

            if (response.Delta is not null)
            {
                if (!string.IsNullOrEmpty(response.Delta.Text))
                    update.Contents.Add(new Microsoft.Extensions.AI.TextContent(response.Delta.Text));

                if (!string.IsNullOrEmpty(response.Delta.Thinking))
                    thinking += response.Delta.Thinking;

                if (!string.IsNullOrEmpty(response.Delta.Signature))
                    update.Contents.Add(new TextReasoningContent(thinking) { ProtectedData = response.Delta.Signature });

                if (response.Delta?.StopReason is string stopReason)
                {
                    update.FinishReason = stopReason switch
                    {
                        "max_tokens" => ChatFinishReason.Length,
                        _ => ChatFinishReason.Stop,
                    };
                }

                if (response.Usage is { } usage)
                    update.Contents.Add(new UsageContent(ChatClientHelper.CreateUsageDetails(usage)));
            }

            if (response.ToolCalls is { Count: > 0 })
            {
                foreach (var f in response.ToolCalls)
                {
                    update.Contents.Add(new FunctionCallContent(f.Id, f.Name,
                        !string.IsNullOrEmpty(f.Arguments?.ToString())
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(f.Arguments!.ToString())
                            : new Dictionary<string, object?>()));
                }
            }

            yield return update;
        }
    }

    // Converts the abstract message list into native MessageParameters, turns on the SDK's
    // automatic system+tools caching, and adds one more breakpoint on the tail of the
    // conversation so the growing history prefix gets cached too.
    private MessageParameters BuildParameters(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var parameters = ChatClientHelper.CreateMessageParameters(client.Messages, messages, options);
        parameters.PromptCaching = PromptCacheType.AutomaticToolsAndSystem;

        var lastContent = parameters.Messages?.LastOrDefault(m => m.Content is { Count: > 0 })?.Content?[^1];
        if (lastContent is { CacheControl: null })
            lastContent.CacheControl = new CacheControl { Type = CacheControlType.ephemeral };

        return parameters;
    }

    public void Dispose() => client.Dispose();

    public object? GetService(Type serviceType, object? serviceKey) =>
        serviceKey is not null ? null :
        serviceType == typeof(ChatClientMetadata) ? (_metadata ??= new(nameof(AnthropicClient))) :
        serviceType?.IsInstanceOfType(this) is true ? this :
        null;
}
