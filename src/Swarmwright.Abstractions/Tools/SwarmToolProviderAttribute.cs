using Microsoft.Extensions.DependencyInjection;

namespace Swarmwright.Tools;

/// <summary>
/// Declares the DI lifetime for an auto-discovered <see cref="ICustomToolProvider"/>
/// implementation. Consumers place this on their provider class so the framework
/// registers it with the appropriate lifetime — <see cref="ServiceLifetime.Scoped"/>
/// for providers with scoped dependencies (e.g. <c>DbContext</c>), <see cref="ServiceLifetime.Singleton"/>
/// for stateless providers, and <see cref="ServiceLifetime.Transient"/> (the default)
/// when unspecified.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SwarmToolProviderAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmToolProviderAttribute"/> class.
    /// </summary>
    /// <param name="lifetime">The DI lifetime. Defaults to <see cref="ServiceLifetime.Transient"/>.</param>
    public SwarmToolProviderAttribute(ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        this.Lifetime = lifetime;
    }

    /// <summary>Gets the DI lifetime used when the provider is auto-registered.</summary>
    public ServiceLifetime Lifetime { get; }
}
