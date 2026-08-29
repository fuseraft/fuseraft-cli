using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace fuseraft.Core.Skills;

/// <summary>
/// Provides a throwaway <see cref="AIAgent"/> for CLI commands that need to call
/// <see cref="AgentFileSkillsSource.GetSkillsAsync"/> (or <see cref="AgentSkillsProvider"/>)
/// purely to validate or inspect skill files on disk, with no live model involved
/// (<c>skills add</c>, <c>skills validate</c>, <c>skills list</c>).
///
/// <para>
/// Both <see cref="AgentSkillsSourceContext"/> and <see cref="AIContextProvider.InvokingContext"/>
/// require a non-null <see cref="AIAgent"/> handle, even though the file-based skills source never
/// reads anything from it — the parameter exists generically across all <see cref="AgentSkillsSource"/>
/// implementations (an MCP-backed source, for instance, might scope skills per agent identity).
/// Where a real <see cref="IChatClient"/> is already in hand (the REPL, <c>SkillCurator</c>),
/// wrap that instead of using this — this stub deliberately can never answer a real prompt.
/// </para>
/// </summary>
public static class SkillDiscoveryAgent
{
    /// <summary>Creates a new throwaway agent backed by a chat client that is never actually invoked.</summary>
    public static AIAgent Create() => new ChatClientAgent(new NonInvocableChatClient());

    private sealed class NonInvocableChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{nameof(NonInvocableChatClient)} exists only to satisfy an API's AIAgent requirement for offline skill discovery and cannot answer prompts.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{nameof(NonInvocableChatClient)} exists only to satisfy an API's AIAgent requirement for offline skill discovery and cannot answer prompts.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
