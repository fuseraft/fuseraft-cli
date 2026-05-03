using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace fuseraft.Cli;

/// <summary>
/// Bridges Spectre.Console.Cli's DI model to Microsoft.Extensions.DependencyInjection.
/// Pass an instance to <c>new CommandApp(registrar)</c>.
/// </summary>
public sealed class ServiceCollectionRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build()
    {
        // Finalize the service collection and hand a resolver to Spectre.
        var provider = services.BuildServiceProvider();
        return new ServiceProviderResolver(provider);
    }

    public void Register(Type service, Type implementation) =>
        services.AddTransient(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        services.AddSingleton(service, _ => factory());
}

internal sealed class ServiceProviderResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) =>
        type is null ? null : provider.GetService(type);

    public void Dispose()
    {
        if (provider is IDisposable d) d.Dispose();
    }
}
