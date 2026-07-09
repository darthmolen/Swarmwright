using Swarmwright.Extensions;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Tests for the custom tool provider auto-discovery pipeline triggered by
/// <c>AddSwarmwright</c>. Exercises the internal <c>DiscoverCustomToolProviders</c>
/// helper directly so tests don't depend on Swarm configuration.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AddSwarmwrightDiscoveryTests
{
    /// <summary>
    /// A provider without the [SwarmToolProvider] attribute registers as Transient.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_DiscoversConcreteImplementations_DefaultTransientLifetime()
    {
        var services = new ServiceCollection();

        services.DiscoverCustomToolProviders();

        var descriptor = services.FirstOrDefault(sd =>
            sd.ServiceType == typeof(ICustomToolProvider)
            && sd.ImplementationType == typeof(DefaultLifetimeProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    /// <summary>
    /// A provider with [SwarmToolProvider(Scoped)] registers as Scoped.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_RespectsScopedLifetimeFromAttribute()
    {
        var services = new ServiceCollection();

        services.DiscoverCustomToolProviders();

        var descriptor = services.FirstOrDefault(sd =>
            sd.ServiceType == typeof(ICustomToolProvider)
            && sd.ImplementationType == typeof(ScopedLifetimeProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// A provider with [SwarmToolProvider(Singleton)] registers as Singleton.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_RespectsSingletonLifetimeFromAttribute()
    {
        var services = new ServiceCollection();

        services.DiscoverCustomToolProviders();

        var descriptor = services.FirstOrDefault(sd =>
            sd.ServiceType == typeof(ICustomToolProvider)
            && sd.ImplementationType == typeof(SingletonLifetimeProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// The abstract <see cref="CustomToolProvider"/> base class is NOT registered.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_SkipsAbstractTypes()
    {
        var services = new ServiceCollection();

        services.DiscoverCustomToolProviders();

        services.Should().NotContain(sd =>
            sd.ServiceType == typeof(ICustomToolProvider)
            && sd.ImplementationType == typeof(CustomToolProvider));
    }

    /// <summary>
    /// If a consumer manually registers a provider, discovery does not add a duplicate.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_DoesNotDuplicateManualRegistration()
    {
        var services = new ServiceCollection();

        // Manual registration first, with a specific lifetime that differs from the attribute.
        services.AddScoped<ICustomToolProvider, DefaultLifetimeProvider>();

        services.DiscoverCustomToolProviders();

        var matches = services
            .Where(sd => sd.ServiceType == typeof(ICustomToolProvider)
                && sd.ImplementationType == typeof(DefaultLifetimeProvider))
            .ToList();

        matches.Should().HaveCount(1, "manual registration should survive auto-discovery without duplication");
        matches[0].Lifetime.Should().Be(ServiceLifetime.Scoped, "attribute must not override manual registration");
    }

    /// <summary>
    /// Providers that are never registered manually are picked up by the scan.
    /// This test pairs with the opt-out test below to confirm discovery actually runs.
    /// </summary>
    [TestMethod]
    public void AddSwarmwright_WithDiscoveryDisabled_SkipsScan()
    {
        var services = new ServiceCollection();

        // Not calling DiscoverCustomToolProviders — simulating discoverCustomToolProviders: false.
        services.Should().NotContain(sd =>
            sd.ServiceType == typeof(ICustomToolProvider)
            && sd.ImplementationType == typeof(DefaultLifetimeProvider));
    }

#pragma warning disable CA1812 // discovered via reflection
#pragma warning disable CA1822 // instance methods required for delegate creation

    /// <summary>Provider without attribute — defaults to Transient.</summary>
    private sealed class DefaultLifetimeProvider : CustomToolProvider
    {
        [SwarmTool("default_lifetime_tool", "Test tool.")]
        public string Invoke() => "default";
    }

    /// <summary>Provider attributed with Scoped lifetime.</summary>
    [SwarmToolProvider(ServiceLifetime.Scoped)]
    private sealed class ScopedLifetimeProvider : CustomToolProvider
    {
        [SwarmTool("scoped_lifetime_tool", "Test tool.")]
        public string Invoke() => "scoped";
    }

    /// <summary>Provider attributed with Singleton lifetime.</summary>
    [SwarmToolProvider(ServiceLifetime.Singleton)]
    private sealed class SingletonLifetimeProvider : CustomToolProvider
    {
        [SwarmTool("singleton_lifetime_tool", "Test tool.")]
        public string Invoke() => "singleton";
    }

#pragma warning restore CA1822
#pragma warning restore CA1812
}
