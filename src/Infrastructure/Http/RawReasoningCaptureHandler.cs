using System.Text;
using System.Text.Json.Nodes;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure;

/// <summary>
/// Captures raw <c>reasoning_content</c> from non-streaming (JSON) chat completion responses
/// and emits an <c>http_reasoning</c> event to the session event log.
///
/// <para>
/// xAI models populate a <c>choices[*].message.reasoning_content</c> field in the JSON response
/// body. This handler extracts that field at the HTTP layer — before the OpenAI SDK deserializes
/// the response — so the raw wire-level text can be compared against what
/// <c>TextReasoningContent</c> surfaces after SDK processing.
/// </para>
///
/// <para>
/// Positioning in the handler chain: inner to <see cref="FinishReasonNormalizerHandler"/> so
/// it sees the body before that handler consumes the stream. After reading, it rebuilds
/// <c>response.Content</c> as a <see cref="StringContent"/> so the outer handlers can still
/// read the body.
/// </para>
///
/// <para>
/// Skips SSE (streaming) responses — those do not carry <c>message.reasoning_content</c>.
/// Emits fire-and-forget: never throws, never blocks the request pipeline.
/// </para>
/// </summary>
internal sealed class RawReasoningCaptureHandler(EventEmitter? eventEmitter) : DelegatingHandler
{
    private const int MaxReasoningChars = 16_000;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content is null || eventEmitter is null) return response;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return response;

        // Read and buffer the body so this handler AND the outer FinishReasonNormalizerHandler
        // can both consume it (the underlying stream from TransientRetryHandler is read-once).
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Content = new StringContent(body, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");

        // Fire-and-forget — EmitAsync never throws.
        TryCaptureReasoning(body, request.RequestUri?.Host ?? "unknown");

        return response;
    }

    private void TryCaptureReasoning(string body, string host)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return;

            var model   = node["model"]?.GetValue<string>();
            var choices = node["choices"]?.AsArray();
            if (choices is null) return;

            int? reasoningTokens = null;
            try
            {
                reasoningTokens = node["usage"]?
                    ["completion_tokens_details"]?
                    ["reasoning_tokens"]?
                    .GetValue<int>();
            }
            catch { /* field absent or wrong type — leave null */ }

            var sb = new StringBuilder();
            foreach (var choice in choices)
            {
                var rc = choice?["message"]?["reasoning_content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(rc)) sb.Append(rc);
            }

            if (sb.Length == 0) return;

            var text = sb.ToString();
            var truncated = text.Length > MaxReasoningChars
                ? text[..MaxReasoningChars] + $"\n[TRUNCATED — {text.Length:N0} chars total]"
                : text;

            _ = eventEmitter!.EmitAsync("http_reasoning",
                agent:   null,
                turn:    null,
                payload: new
                {
                    model,
                    source           = "reasoning_content",
                    text             = truncated,
                    reasoning_tokens = reasoningTokens,
                    host,
                });
        }
        catch { /* never let capture crash the request pipeline */ }
    }
}
