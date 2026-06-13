namespace fuseraft.Infrastructure.Context;

/// <summary>
/// Ambient call-sequence number that flows from the per-inner-call middleware in
/// <see cref="AgentFactory"/> through to <see cref="RawReasoningCaptureHandler"/> via
/// C#'s async execution-context inheritance.
///
/// <para>
/// Set to the current <c>innerCallSeq</c> value immediately before every
/// <c>inner.GetResponseAsync</c> call in the middleware closure. Because
/// <see cref="AsyncLocal{T}"/> values propagate <em>downward</em> (parent → child) but
/// not back up, the value is visible inside <see cref="RawReasoningCaptureHandler.SendAsync"/>
/// for that specific HTTP call.
/// </para>
///
/// <para>
/// Sub-agent HTTP calls never see the main-agent's sequence number. The
/// <see cref="FunctionInvokingChatClient"/> executes tool calls within its own execution
/// context (captured before our middleware ran), so any sub-agent that spawns HTTP requests
/// reads <see langword="null"/> here — making sub-agent and main-agent calls distinguishable
/// in <c>http_reasoning</c> events without any explicit clearing.
/// </para>
/// </summary>
internal static class InnerCallId
{
    internal static readonly AsyncLocal<int?> Current = new();
}
