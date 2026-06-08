using Swarmwright.Extensions;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Verifies the DI registration of the scoped run-context holder: the public
/// <see cref="ISwarmRunContext"/> and the concrete <see cref="SwarmRunContext"/>
/// resolve to the same per-scope instance, and different scopes get different
/// instances.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmRunContextRegistrationShould
{
    private ServiceProvider provider = null!;

    /// <summary>Disposes the provider after each test.</summary>
    [TestCleanup]
    public void Cleanup()
    {
        this.provider?.Dispose();
    }

    /// <summary>
    /// Within one scope, the interface and the concrete type resolve to the
    /// same instance.
    /// </summary>
    [TestMethod]
    public void ResolveInterfaceAndConcreteToSameInstanceWithinScope()
    {
        this.provider = BuildProvider();

        using var scope = this.provider.CreateScope();
        var asInterface = scope.ServiceProvider.GetRequiredService<ISwarmRunContext>();
        var asConcrete = scope.ServiceProvider.GetRequiredService<SwarmRunContext>();

        asInterface.Should().BeSameAs(asConcrete);
    }

    /// <summary>
    /// Two different scopes resolve two different holder instances.
    /// </summary>
    [TestMethod]
    public void ResolveDifferentInstancesAcrossScopes()
    {
        this.provider = BuildProvider();

        using var scopeA = this.provider.CreateScope();
        using var scopeB = this.provider.CreateScope();

        var fromA = scopeA.ServiceProvider.GetRequiredService<SwarmRunContext>();
        var fromB = scopeB.ServiceProvider.GetRequiredService<SwarmRunContext>();

        fromA.Should().NotBeSameAs(fromB);
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Database:Provider"] = "InMemory",
                ["Swarm:TemplatesDirectory"] = "templates",
            })
            .Build();

        var services = new ServiceCollection();
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Testing");

        services.AddSwarmDomain(configuration, env.Object);

        return services.BuildServiceProvider();
    }
}
