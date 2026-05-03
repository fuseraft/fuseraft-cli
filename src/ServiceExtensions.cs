using AgentGovernance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Strategies;

namespace fuseraft;

public static class ServiceExtensions
{
    /// <summary>
    /// Registers the full orchestration stack from the "Orchestration" config section.
    /// </summary>
    public static IServiceCollection AddOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        var config = configuration.GetSection("Orchestration").Get<OrchestrationConfig>()
            ?? throw new InvalidOperationException("Required config section 'Orchestration' is missing or empty.");

        if (config.Agents.Count == 0)
            throw new InvalidOperationException("Orchestration config must define at least one agent.");

        services.AddSingleton(config);
        services.AddSingleton(_ => new GovernanceKernel(new GovernanceOptions
        {
            EnableAudit   = true,
            EnableMetrics = true,
        }));
        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton(static _ => new PluginRegistry().RegisterDefaults());
        services.AddSingleton<AgentFactory>();
        services.AddSingleton<StrategyFactory>();
        services.AddSingleton<IOrchestrator, AgentOrchestrator>();
        services.AddSingleton<ISessionStore, JsonSessionStore>();

        return services;
    }
}
