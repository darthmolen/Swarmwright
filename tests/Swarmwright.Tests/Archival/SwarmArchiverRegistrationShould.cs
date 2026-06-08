using Swarmwright.Archival;
using Swarmwright.Events;
using Swarmwright.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Swarmwright.Tests.Archival;

/// <summary>
/// Tests for archiver DI registration in <c>AddSwarmDomain</c> — disabled
/// archival must resolve a no-op implementation.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmArchiverRegistrationShould
{
    /// <summary>
    /// Verifies that when <c>Swarm:Archival:Enabled</c> is false (the default),
    /// the resolved <see cref="ISwarmRunArchiver"/> is the no-op implementation.
    /// </summary>
    [TestMethod]
    public void ResolveNoOpArchiverWhenDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Archival:Enabled"] = "false",
                ["Swarm:Database:Provider"] = "InMemory",
            })
            .Build();

        var environment = new TestHostEnvironment("Testing");

        var services = new ServiceCollection();
        services.AddSwarmDomain(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var archiver = provider.GetRequiredService<ISwarmRunArchiver>();

        archiver.Should().BeOfType<NoOpSwarmRunArchiver>(
            "disabled archival must register the no-op archiver.");
    }

    /// <summary>
    /// Verifies the in-process notification pipeline is wired: a publisher, the background
    /// drain service, and the run-completed archival handler are all registered by
    /// <c>AddSwarmDomain</c> so the terminal completion notification reaches the archiver.
    /// </summary>
    [TestMethod]
    public void RegisterNotificationPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Archival:Enabled"] = "false",
                ["Swarm:Database:Provider"] = "InMemory",
            })
            .Build();

        var environment = new TestHostEnvironment("Testing");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmDomain(configuration, environment);

        using var provider = services.BuildServiceProvider();

        provider.GetService<ISwarmNotificationPublisher>()
            .Should().NotBeNull("AddSwarmDomain must register the notification publisher.");

        provider.GetServices<IHostedService>()
            .Should().Contain(
                s => s is SwarmNotificationBackgroundService,
                "the background drain service must be registered to dispatch notifications off-thread.");

        var handlers = provider
            .GetServices<ISwarmNotificationHandler<SwarmRunCompletedNotification>>()
            .ToList();

        handlers.Should().ContainSingle(
            "the archival handler must be registered exactly once via TryAddEnumerable.")
            .Which.Should().BeOfType<SwarmRunCompletedNotificationConsumer>();
    }

    /// <summary>
    /// Guards TryAddEnumerable idempotency: registering the archival handler again before
    /// <c>AddSwarmDomain</c> still yields exactly one handler, never a duplicate.
    /// </summary>
    [TestMethod]
    public void RegisterArchivalHandlerExactlyOnceWhenAlreadyPresent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Archival:Enabled"] = "false",
                ["Swarm:Database:Provider"] = "InMemory",
            })
            .Build();

        var environment = new TestHostEnvironment("Testing");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ISwarmNotificationHandler<SwarmRunCompletedNotification>, SwarmRunCompletedNotificationConsumer>();
        services.AddSwarmDomain(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var handlers = provider
            .GetServices<ISwarmNotificationHandler<SwarmRunCompletedNotification>>()
            .ToList();

        handlers.Should().ContainSingle(
            "the handler must be registered exactly once, not duplicated by AddSwarmDomain.")
            .Which.Should().BeOfType<SwarmRunCompletedNotificationConsumer>();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            this.EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
