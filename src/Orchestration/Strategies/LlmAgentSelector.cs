using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>LLM-based agent selector — calls an IChatClient to pick the next agent.</summary>
internal sealed class LlmAgentSelector(
    IChatClient chatClient,
    string promptTemplate) : IAgentSelector
{
    public async Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var agentNames = string.Join(", ", agents.Select(a => a.Name));
        var historyText = string.Join("\n",
            history.TakeLast(20)
                   .Where(m => !string.IsNullOrEmpty(m.Text))
                   .Select(m => $"{m.AuthorName ?? m.Role.Value}: {m.Text}"));

        var prompt = promptTemplate
            .Replace("{{$agents}}", agentNames)
            .Replace("{{$history}}", historyText);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: cancellationToken);

        var name = response.Text?.Trim() ?? string.Empty;
        var matched = agents.FirstOrDefault(
            a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        return matched ?? (agents.Count > 0 ? agents[0] : null);
    }
}
