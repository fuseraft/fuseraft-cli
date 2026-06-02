using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<MemoryManager>? _logger;
    private RepositoryMemoryStore? _repositoryStore;

    public MemoryManager(IReadOnlyList<IMemoryProvider> providers, ILogger<MemoryManager>? logger = null)
    {
        _providers = providers;
        _logger    = logger;
    }

    /// <summary>
    /// Attaches a <see cref="RepositoryMemoryStore"/> so that <c>Approved</c>,
    /// high-confidence repository memories are injected into agent prompts via
    /// <see cref="PreTurnAsync"/>. Call this after construction when the store is
    /// available (e.g. from <c>OrchestratorBuilder</c>).
    /// </summary>
    public void AttachRepositoryMemory(RepositoryMemoryStore store) => _repositoryStore = store;

    /// <summary>
    /// Builds a <see cref="MemoryManager"/> from orchestration config.
    /// Returns <see langword="null"/> when <paramref name="cfg"/> is null or the provider
    /// name is unrecognised.
    /// </summary>
    public static MemoryManager? FromConfig(MemoryConfig? cfg, ILogger<MemoryManager>? logger = null)
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
            logger?.LogWarning(
                "MemoryManager: unknown or misconfigured provider '{Provider}' — memory disabled.", cfg.Provider);
            return null;
        }

        return new MemoryManager([provider], logger);
    }

    /// <summary>
    /// Called before each agent turn.
    /// Returns a memory block to prepend to the agent's system instructions,
    /// or <see langword="null"/> when no memory applies.
    /// Includes <c>Approved</c>, high-confidence repository memories when a
    /// <see cref="RepositoryMemoryStore"/> has been attached via <see cref="AttachRepositoryMemory"/>.
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
                _logger?.LogWarning(ex, "MemoryManager: provider load error for '{Agent}'.", agentName);
            }
        }

        // Repository scope: inject Approved, high-confidence entries only.
        if (_repositoryStore is not null)
        {
            try
            {
                var approved = await _repositoryStore.LoadApprovedAsync(ct);
                var highConf = approved.Where(e =>
                    e.Confidence.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                    e.Confidence.Equals("Inferred", StringComparison.OrdinalIgnoreCase)).ToList();

                if (highConf.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("REPOSITORY MEMORY — patterns observed across sessions:");
                    foreach (var m in highConf.OrderByDescending(m => m.ReinforcementCount).Take(20))
                        sb.AppendLine($"  [{m.Confidence}] (×{m.ReinforcementCount}) {m.Pattern}");
                    blocks.Add(sb.ToString().TrimEnd());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryManager: repository memory load error.");
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
                _logger?.LogWarning(ex, "MemoryManager: provider save error for '{Agent}'.", agentName);
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
