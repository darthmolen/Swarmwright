using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Templates;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Extensions;

[TestClass]
public class SwarmServiceExtensionsTests
{
    private ServiceProvider provider = null!;

    [TestCleanup]
    public void Cleanup()
    {
        this.provider?.Dispose();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersSwarmOptions()
    {
        this.provider = this.BuildProvider();

        var options = this.provider.GetRequiredService<IOptions<SwarmOptions>>();

        options.Should().NotBeNull();
        options.Value.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersDbContextFactory()
    {
        this.provider = this.BuildProvider();

        var factory = this.provider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();

        factory.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersRepository()
    {
        this.provider = this.BuildProvider();

        using var scope = this.provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISwarmRepository>();

        repository.Should().NotBeNull();
        repository.Should().BeOfType<SwarmRepository>();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersSwarmManager()
    {
        this.provider = this.BuildProvider();

        var manager = this.provider.GetRequiredService<ISwarmManager>();

        manager.Should().NotBeNull();
        manager.Should().BeOfType<SwarmManager>();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersEventBus()
    {
        this.provider = this.BuildProvider();

        var eventBus = this.provider.GetRequiredService<ISwarmEventBus>();

        eventBus.Should().NotBeNull();
        eventBus.Should().BeOfType<SwarmEventBus>();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersInboxSystem()
    {
        this.provider = this.BuildProvider();

        var inbox = this.provider.GetRequiredService<IInboxSystem>();

        inbox.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersTeamRegistry()
    {
        this.provider = this.BuildProvider();

        var registry = this.provider.GetRequiredService<ITeamRegistry>();

        registry.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersTemplateLoader()
    {
        this.provider = this.BuildProvider();

        var loader = this.provider.GetRequiredService<ITemplateLoader>();

        loader.Should().NotBeNull();
        loader.Should().BeOfType<TemplateLoader>();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersChannelReader()
    {
        this.provider = this.BuildProvider();

        var reader = this.provider.GetRequiredService<ChannelReader<SwarmRequest>>();

        reader.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersChannelWriter()
    {
        this.provider = this.BuildProvider();

        var writer = this.provider.GetRequiredService<ChannelWriter<SwarmRequest>>();

        writer.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_RegistersActiveSwarmsDictionary()
    {
        this.provider = this.BuildProvider();

        var dict = this.provider.GetRequiredService<ConcurrentDictionary<Guid, SwarmExecution>>();

        dict.Should().NotBeNull();
    }

    [TestMethod]
    public void AddSwarmDomain_ThrowsForUnsupportedProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Database:Provider"] = "Oracle",
            })
            .Build();

        var services = new ServiceCollection();
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Testing");

        var act = () => services.AddSwarmDomain(configuration, env.Object);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Oracle*");
    }

    [TestMethod]
    public void AddSwarmDomain_ThrowsForNullConfiguration()
    {
        var services = new ServiceCollection();
        var env = new Mock<IHostEnvironment>();

        var act = () => services.AddSwarmDomain(null!, env.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void AddSwarmDomain_ThrowsForNullHostingEnvironment()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        var act = () => services.AddSwarmDomain(configuration, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Used with this. qualifier per SA1101.")]
    private ServiceProvider BuildProvider(Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Swarm:Database:Provider"] = "InMemory",
            ["Swarm:TemplatesDirectory"] = "templates",
        };

        if (overrides != null)
        {
            foreach (var kv in overrides)
            {
                config[kv.Key] = kv.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Testing");

        services.AddSwarmDomain(configuration, env.Object);

        return services.BuildServiceProvider();
    }
}
