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
        // Probe 1: inspect the outgoing request body before sending.
        // Counts "reasoning_content" occurrences to determine whether ProtectedData
        // is actually serialized into the wire payload, and captures body size.
        // The body is buffered and re-set so the inner handler can still read it.
        int    reqReasoningBlobs = 0;
        long   reqBodyBytes      = 0;
        if (eventEmitter is not null && request.Content is not null)
        {
            try
            {
                var reqBody = await request.Content.ReadAsStringAsync(cancellationToken);
                reqBodyBytes      = Encoding.UTF8.GetByteCount(reqBody);
                reqReasoningBlobs = CountOccurrences(reqBody, "\"reasoning_content\"");
                request.Content   = new StringContent(reqBody, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
            catch { /* never let instrumentation break the pipeline */ }
        }

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
        TryCaptureReasoning(body, request.RequestUri?.Host ?? "unknown",
            reqBodyBytes, reqReasoningBlobs);

        return response;
    }

    // Counts non-overlapping occurrences of a literal substring.
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private void TryCaptureReasoning(string body, string host,
        long reqBodyBytes, int reqReasoningBlobs)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return;

            var model   = node["model"]?.GetValue<string>();
            var choices = node["choices"]?.AsArray();
            if (choices is null) return;

            // Probe 2: extract per-call token usage from the response.
            // prompt_tokens answers "is the turn's InputTokens the final call or cumulative?"
            int? promptTokens     = null;
            int? completionTokens = null;
            int? reasoningTokens  = null;
            try
            {
                var usage          = node["usage"];
                promptTokens       = usage?["prompt_tokens"]?.GetValue<int>();
                completionTokens   = usage?["completion_tokens"]?.GetValue<int>();
                reasoningTokens    = usage?["completion_tokens_details"]?
                                         ["reasoning_tokens"]?.GetValue<int>();
            }
            catch { /* field absent or wrong type — leave null */ }

            var sb = new StringBuilder();
            foreach (var choice in choices)
            {
                var rc = choice?["message"]?["reasoning_content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(rc)) sb.Append(rc);
            }

            // Emit request/response probe even when there is no reasoning text,
            // so every inner API call is represented in the event log.
            var hasReasoning = sb.Length > 0;
            var text = sb.ToString();
            var truncated = text.Length > MaxReasoningChars
                ? text[..MaxReasoningChars] + $"\n[TRUNCATED — {text.Length:N0} chars total]"
                : text;

            _ = eventEmitter!.EmitAsync(EventTypes.HttpReasoning,
                agent:   null,
                turn:    null,
                payload: new
                {
                    model,
                    source              = "reasoning_content",
                    text                = hasReasoning ? truncated : null,
                    reasoning_tokens    = reasoningTokens,
                    host,
                    // Correlates this http_reasoning with the inner_call_context event that preceded
                    // the HTTP call. Null for sub-agent HTTP calls (they inherit FunctionInvokingChatClient's
                    // execution context, which never had the main-agent's call-seq set).
                    call_seq            = InnerCallId.Current.Value,
                    // Request probes — answer: "does ProtectedData reach the wire?"
                    req_body_bytes      = reqBodyBytes,
                    req_reasoning_blobs = reqReasoningBlobs,
                    // Response probes — answer: "is 561K per-call or cumulative?"
                    resp_prompt_tokens      = promptTokens,
                    resp_completion_tokens  = completionTokens,
                });
        }
        catch { /* never let capture crash the request pipeline */ }
    }
}
