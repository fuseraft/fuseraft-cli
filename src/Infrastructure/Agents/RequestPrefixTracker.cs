using System.Text.Json;
using fuseraft.Core;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Finds where each request first differs from the one sent before it. A provider's prompt cache serves a request
/// only up to its first changed message, so the event log can tell a rewritten history from a cold cache.
/// </summary>
internal sealed class RequestPrefixTracker
{
    private readonly Lock _lock = new();
    private int[] _previous = [];

    /// <summary>
    /// The index of the first message that differs from the previous request, or null when every message the two
    /// share is unchanged; and how many messages the previous request had.
    /// </summary>
    public (int? ChangedAt, int PreviousCount) Observe(IEnumerable<ChatMessage> messages)
    {
        var hashes = messages.Select(Hash).ToArray();
        lock (_lock)
        {
            var previous = _previous;
            _previous = hashes;
            var shared = Math.Min(previous.Length, hashes.Length);
            for (var i = 0; i < shared; i++)
                if (previous[i] != hashes[i])
                    return (i, previous.Length);
            return (null, previous.Length);
        }
    }

    private static int Hash(ChatMessage message)
    {
        var hash = new HashCode();
        hash.Add(message.Role);
        foreach (var content in message.Contents)
        {
            hash.Add(content.GetType());
            switch (content)
            {
                case TextContent text:
                    hash.Add(text.Text);
                    break;
                case TextReasoningContent reasoning:
                    hash.Add(reasoning.Text);
                    hash.Add(reasoning.ProtectedData);
                    break;
                case FunctionCallContent call:
                    hash.Add(call.CallId);
                    hash.Add(call.Name);
                    foreach (var (key, value) in call.Arguments ?? new Dictionary<string, object?>())
                    {
                        hash.Add(key);
                        hash.Add(value is JsonElement json ? json.GetRawText() : value?.ToString());
                    }
                    break;
                case FunctionResultContent result:
                    hash.Add(result.CallId);
                    hash.Add(ToolResultText.ToStringOrDefault(result.Result));
                    break;
                case DataContent data:
                    hash.Add(data.MediaType);
                    hash.Add(data.Data.Length);
                    break;
            }
        }
        return hash.ToHashCode();
    }
}

/// <summary>What changed a request's prefix since the previous request in the same conversation.</summary>
internal static class PrefixChangeCause
{
    /// <summary>The tool list changed; providers put tools ahead of every message.</summary>
    public const string Tools = "tools";
    /// <summary>The stored history was rewritten between requests: compaction, history trimming, a resumed or forked chat.</summary>
    public const string History = "history";
    /// <summary>Superseded writes, reads, or shell reruns were dropped from the request.</summary>
    public const string StaleDrop = "stale_drop";
    /// <summary>The request neared its budget and older rounds were shortened.</summary>
    public const string InTurnTrim = "in_turn_trim";
    /// <summary>The provider rejected the request as too large and it was retried with tool results cut down.</summary>
    public const string OverflowTrim = "overflow_trim";
}

/// <summary>How a request differs from the previous one in its conversation.</summary>
internal readonly record struct RequestChange(int? ChangedAt, int PreviousCount, string? Cause, bool ToolsChanged);

/// <summary>
/// Tracks each conversation's requests at three points (as stored, after superseded content is dropped, and as sent)
/// and names which step changed the prefix. The main thread of work and each subagent run are separate conversations:
/// subagent runs share one chat client and may run side by side, but each has its own cache prefix.
/// </summary>
internal sealed class RequestRewriteTracker
{
    private sealed class Conversation
    {
        public readonly RequestPrefixTracker Stored = new(), Dropped = new(), Sent = new();
        public bool StoredChanged, DroppedChanged;
        public int? ToolsHash;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Conversation> _conversations = new();

    private Conversation Current(string? run) => _conversations.GetOrAdd(run ?? "", _ => new Conversation());

    /// <summary>Records the request as the history stored it and after superseded content was dropped.</summary>
    public void ObserveInputs(string? run, IEnumerable<ChatMessage> stored, IEnumerable<ChatMessage> dropped)
    {
        var conversation = Current(run);
        lock (conversation)
        {
            conversation.StoredChanged  = conversation.Stored.Observe(stored).ChangedAt is not null;
            conversation.DroppedChanged = conversation.Dropped.Observe(dropped).ChangedAt is not null;
        }
    }

    /// <summary>Records the request as sent and says what, if anything, changed its prefix.</summary>
    public RequestChange ObserveSent(string? run, IEnumerable<ChatMessage> sent, IList<AITool>? tools, int attempt)
    {
        var conversation = Current(run);
        lock (conversation)
        {
            var (changedAt, previousCount) = conversation.Sent.Observe(sent);
            var toolsHash = HashTools(tools);
            var toolsChanged = conversation.ToolsHash is { } previous && previous != toolsHash;
            conversation.ToolsHash = toolsHash;

            var cause = toolsChanged ? PrefixChangeCause.Tools
                : changedAt is null ? null
                : attempt > 0 ? PrefixChangeCause.OverflowTrim
                : conversation.StoredChanged ? PrefixChangeCause.History
                : conversation.DroppedChanged ? PrefixChangeCause.StaleDrop
                : PrefixChangeCause.InTurnTrim;
            return new RequestChange(changedAt, previousCount, cause, toolsChanged);
        }
    }

    private static int HashTools(IList<AITool>? tools)
    {
        var hash = new HashCode();
        foreach (var tool in tools ?? [])
        {
            hash.Add(tool.Name);
            hash.Add(tool.Description);
        }
        return hash.ToHashCode();
    }
}
