using System.Net;
using System.Net.Http.Json;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Verifies that the HTTP create endpoint forwards the optional context bag from
/// <c>CreateSwarmRequest</c> to <see cref="ISwarmManager.CreateSwarmAsync"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmCreateEndpointContextTests
{
    private WebApplication app = null!;
    private HttpClient client = null!;
    private string workBasePath = null!;
    private readonly Mock<ISwarmManager> manager = new();
    private IReadOnlyDictionary<string, string>? captured;

    /// <summary>Spins up an in-process host with a mock manager that captures context.</summary>
    /// <returns>A task representing the asynchronous setup.</returns>
    [TestInitialize]
    public async Task Initialize()
    {
        this.workBasePath = Path.Combine(Path.GetTempPath(), "swarm-create-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workBasePath);

        this.manager
            .Setup(m => m.CreateSwarmAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string?, IReadOnlyDictionary<string, string>?>((_, _, ctx) => this.captured = ctx)
            .ReturnsAsync(Guid.NewGuid());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.EnvironmentName = "UnitTest";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swarm:Database:Provider"] = "InMemory",
                ["Swarm:TemplatesDirectory"] = "templates",
                ["Swarm:WorkBasePath"] = this.workBasePath,
            })
            .Build();

        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddSwarmDomain(configuration, builder.Environment);
        builder.Services.AddSwarmHttpServices();
        builder.Services.AddSingleton(this.manager.Object);

        this.app = builder.Build();
        this.app.MapSwarmEndpoints(useSwarmPolicies: false);
        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();
    }

    /// <summary>Tears down the host.</summary>
    /// <returns>A task representing the asynchronous cleanup.</returns>
    [TestCleanup]
    public async Task Cleanup()
    {
        this.client?.Dispose();
        if (this.app is not null)
        {
            await this.app.StopAsync().ConfigureAwait(false);
            await this.app.DisposeAsync().ConfigureAwait(false);
        }

        if (Directory.Exists(this.workBasePath))
        {
            try
            {
                Directory.Delete(this.workBasePath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Posting a create request with a context bag forwards it to the manager.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [TestMethod]
    public async Task PostCreate_ForwardsContext_ToManager()
    {
        var payload = new
        {
            goal = "build a thing",
            templateKey = (string?)null,
            context = new Dictionary<string, string> { ["sourceRoot"] = "/clones/pr-9" },
        };

        var response = await this.client
            .PostAsJsonAsync(new Uri("/api/swarm/", UriKind.Relative), payload)
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        this.captured.Should().NotBeNull();
        this.captured.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-9");
    }
}
