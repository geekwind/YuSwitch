using Microsoft.Extensions.DependencyInjection;
using YuSwitch.Data.Entities;

namespace YuSwitch.Providers;

/// <summary>
/// Factory that builds an IProvider instance from a ServiceEntity.
/// Each provider type registers a factory; the registry looks up by type.
/// </summary>
public delegate IProvider ProviderFactory(ServiceEntity service, IServiceProvider sp);

/// <summary>
/// Registry of provider factories by type name. Providers register via
/// AddProvider&lt;T&gt; at DI setup time. The registry is the single dispatch
/// point replacing the legacy Go serviceHandlerMap.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Registers a factory under a provider type name.</summary>
    void Register(string type, ProviderFactory factory);

    /// <summary>Builds a provider instance for the given service config.</summary>
    IProvider Create(ServiceEntity service);

    /// <summary>All registered provider type names.</summary>
    IReadOnlyCollection<string> RegisteredTypes { get; }
}

public class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ProviderFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _sp;

    public ProviderRegistry(IServiceProvider sp) => _sp = sp;

    public void Register(string type, ProviderFactory factory) =>
        _factories[type] = factory;

    public IProvider Create(ServiceEntity service)
    {
        if (!_factories.TryGetValue(service.ProviderType, out var factory))
            throw new InvalidOperationException(
                $"No provider registered for type '{service.ProviderType}'. " +
                $"Registered: {string.Join(", ", _factories.Keys)}");
        return factory(service, _sp);
    }

    public IReadOnlyCollection<string> RegisteredTypes => _factories.Keys;
}
