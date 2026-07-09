using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Tests for <see cref="SwarmToolProviderAttribute"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmToolProviderAttributeTests
{
    /// <summary>
    /// The no-arg constructor defaults lifetime to Transient.
    /// </summary>
    [TestMethod]
    public void Constructor_DefaultsToTransient()
    {
        var attribute = new SwarmToolProviderAttribute();

        attribute.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    /// <summary>
    /// An explicit lifetime argument is preserved.
    /// </summary>
    [TestMethod]
    public void Constructor_SetsExplicitLifetime()
    {
        var scoped = new SwarmToolProviderAttribute(ServiceLifetime.Scoped);
        var singleton = new SwarmToolProviderAttribute(ServiceLifetime.Singleton);

        scoped.Lifetime.Should().Be(ServiceLifetime.Scoped);
        singleton.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
