using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Aggregates one or more <see cref="IMemoryProvider"/> instances and exposes
/// pre- and post-turn hooks that the orchestrator calls around each agent turn.
/// All provider errors are caught and logged so a memory failure never interrupts a session.
/// Only <see cref="OperationCanceledException"/> propagates.
/// </summary>
public sealed class MemoryManager : IDisposable
{
    private readonly IReadOnlyList<IMemoryProvider> _providers;

    public MemoryManager(IReadOnlyList<IMemoryProvider> providers)
        => _providers = providers;

    /// <summary>
    /// Builds a <see cref="MemoryManager"/> from orchestration config.
    /// Returns <see langword="null"/> when <paramref name="cfg"/> is null or the provider
    /// name is unrecognised.
    /// </summary>
    public static MemoryManager? FromConfig(MemoryConfig? cfg)
    {
        if (cfg is null) return null;

        IMemoryProvider? provider = cfg.Provider.ToLowerInvariant() switch
        {
            "local"   => new LocalMemoryProvider(),
            "webhook" => cfg.Webhook is not null ? new WebhookMemoryProvider(cfg.Webhook) : null,
            _         => null,
        };

        if (provider is null)
        {
            Console.Error.WriteLine($"[MemoryManager] Unknown or misconfigured memory provider '{cfg.Provider}' — memory disabled.");
            return null;
        }

        return new MemoryManager([provider]);
    }

    /// <summary>
    /// Called before each agent turn.
    /// Returns a memory block to prepend to the agent's system instructions,
    /// or <see langword="null"/> when no memory applies.
    /// </summary>
    public async Task<string?> PreTurnAsync(string agentName, CancellationToken ct = default)
    {
        var blocks = new List<string>();

        foreach (var p in _providers)
        {
            try
            {
                var block = await p.LoadAsync(agentName, ct);
                if (!string.IsNullOrWhiteSpace(block))
                    blocks.Add(block);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MemoryManager] Provider load error for '{agentName}': {ex.Message}");
            }
        }

        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }

    /// <summary>
    /// Called after each agent turn with the accumulated history.
    /// </summary>
    public async Task PostTurnAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        foreach (var p in _providers)
        {
            try
            {
                await p.SaveAsync(agentName, history, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MemoryManager] Provider save error for '{agentName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns a copy of the agent's system instructions augmented with the memory block.
    /// When no memory applies, returns the original instructions unchanged.
    /// </summary>
    public async Task<string?> AugmentInstructionsAsync(string agentName, string? instructions, CancellationToken ct = default)
    {
        var block = await PreTurnAsync(agentName, ct);
        if (block is null) return instructions;

        return string.IsNullOrWhiteSpace(instructions)
            ? block
            : $"{instructions}\n\n{block}";
    }

    public void Dispose()
    {
        foreach (var p in _providers)
            (p as IDisposable)?.Dispose();
    }
}
