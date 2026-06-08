using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Integration tests for the swarm artifact endpoints at <c>/api/swarm/{id}/artifacts</c>.
/// Uses an in-process <see cref="TestServer"/> to exercise the real endpoint mapping.
/// </summary>
[TestClass]
public sealed class ArtifactEndpointTests
{
    private WebApplication app = null!;
    private HttpClient client = null!;
    private string workBasePath = null!;

    /// <summary>
    /// Creates a fresh in-process web host and test client before each test.
    /// </summary>
    /// <returns>A task representing the asynchronous setup operation.</returns>
    [TestInitialize]
    public async Task Initialize()
    {
        this.workBasePath = Path.Combine(Path.GetTempPath(), "swarm-artifact-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workBasePath);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Avoid "Testing" — that environment triggers SwarmMigrationRunner which calls
        // Database.MigrateAsync and fails on the InMemory provider used here.
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

        this.app = builder.Build();
        this.app.MapSwarmEndpoints(useSwarmPolicies: false);

        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();
    }

    /// <summary>
    /// Disposes the in-process web host and test client after each test.
    /// </summary>
    /// <returns>A task representing the asynchronous cleanup operation.</returns>
    [TestCleanup]
    public async Task Cleanup()
    {
        this.client?.Dispose();
        if (this.app is not null)
        {
            await this.app.StopAsync().ConfigureAwait(false);
            await this.app.DisposeAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(this.workBasePath) && Directory.Exists(this.workBasePath))
        {
            try
            {
                Directory.Delete(this.workBasePath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup — ignore transient file locks.
            }
        }
    }

    /// <summary>
    /// Verifies that listing artifacts for an empty work directory returns an empty collection.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifacts_EmptyWorkDirectory_ReturnsEmptyList()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// Verifies that listing artifacts returns each written file with its relative path and byte size.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifacts_WithWrittenFiles_ReturnsFilePaths()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);

        await File.WriteAllTextAsync(Path.Combine(workDir, "report.md"), "hello world").ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(workDir, "notes"));
        await File.WriteAllTextAsync(Path.Combine(workDir, "notes", "a.txt"), "hi").ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var files = doc.RootElement.GetProperty("files");
        files.GetArrayLength().Should().Be(2);

        var entries = files.EnumerateArray().Select(e => new
        {
            Name = e.GetProperty("name").GetString(),
            Path = e.GetProperty("path").GetString()!.Replace('\\', '/'),
            Size = e.GetProperty("size").GetInt64(),
        }).ToList();

        entries.Should().ContainEquivalentOf(new { Name = "report.md", Path = "report.md", Size = 11L });
        entries.Should().ContainEquivalentOf(new { Name = "a.txt", Path = "notes/a.txt", Size = 2L });
    }

    /// <summary>
    /// Verifies that fetching an existing artifact by path returns its content.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifactByPath_ExistingFile_ReturnsContent()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        await File.WriteAllTextAsync(Path.Combine(workDir, "report.md"), "the content").ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts/report.md", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Be("the content");
    }

    /// <summary>
    /// Verifies that a traversal attempt in the path segment is rejected with 400.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifactByPath_TraversalAttempt_Returns400()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);

        // Write a sibling file one level above the swarm work dir so a successful traversal
        // resolution would leak a file outside the sandbox; the endpoint must reject it first.
        var parentDir = Directory.GetParent(this.GetWorkDirectory(swarmId))!.FullName;
        var escapeTarget = Path.Combine(parentDir, "escape.txt");
        await File.WriteAllTextAsync(escapeTarget, "secret").ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/api/swarm/{swarmId}/artifacts/..%2Fescape.txt", UriKind.Relative));
        var response = await this.client.SendAsync(request).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Verifies that an unknown swarm id returns 404.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifactByPath_UnknownSwarmId_Returns404()
    {
        var unknownId = Guid.NewGuid();

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{unknownId}/artifacts/anything.txt", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies that the download-zip endpoint streams a valid zip archive containing all files.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task DownloadZip_StreamsAllFiles()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        await File.WriteAllTextAsync(Path.Combine(workDir, "one.txt"), "one").ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(workDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workDir, "sub", "two.txt"), "two").ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts/download-zip", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        entryNames.Should().Contain("one.txt");
        entryNames.Should().Contain("sub/two.txt");
    }

    /// <summary>
    /// Verifies that GET artifacts returns 404 when the swarm's work directory no longer exists.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifacts_WhenWorkDirectoryDeleted_Returns404()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        Directory.Delete(workDir, recursive: true);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies that download-zip returns 404 when the swarm's work directory no longer exists.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task DownloadZip_WhenWorkDirectoryDeleted_Returns404()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        Directory.Delete(workDir, recursive: true);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts/download-zip", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies that after the swarm is evicted from the in-memory active-swarms dictionary
    /// (the 5-minute eviction path), the artifacts list endpoint still returns files from
    /// the on-disk work directory. This is the scenario the user hit: the work directory at
    /// swarm-workdirs/{id} exists, but the endpoint returned 404 because it relied on the
    /// in-memory execution.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifacts_AfterSwarmEviction_StillReturnsFilesFromDisk()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        await File.WriteAllTextAsync(Path.Combine(workDir, "report.md"), "hello").ConfigureAwait(false);

        // Simulate 5-minute eviction: the execution is removed from active-swarms but the
        // directory on disk under WorkBasePath/{swarmId} persists.
        var activeSwarms = this.app.Services.GetRequiredService<ConcurrentDictionary<Guid, SwarmExecution>>();
        activeSwarms.TryRemove(swarmId, out _).Should().BeTrue();

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("files").GetArrayLength().Should().Be(1);
    }

    /// <summary>
    /// Verifies that after swarm eviction, fetching an individual artifact by path still
    /// returns its content from the on-disk work directory.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifactByPath_AfterSwarmEviction_StillReturnsContent()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        await File.WriteAllTextAsync(Path.Combine(workDir, "report.md"), "the content").ConfigureAwait(false);

        var activeSwarms = this.app.Services.GetRequiredService<ConcurrentDictionary<Guid, SwarmExecution>>();
        activeSwarms.TryRemove(swarmId, out _).Should().BeTrue();

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts/report.md", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Be("the content");
    }

    /// <summary>
    /// Verifies that after swarm eviction, the download-zip endpoint still streams the archive
    /// from the on-disk work directory.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task DownloadZip_AfterSwarmEviction_StillStreamsArchive()
    {
        var swarmId = await this.CreateSwarmAsync().ConfigureAwait(false);
        var workDir = this.GetWorkDirectory(swarmId);
        await File.WriteAllTextAsync(Path.Combine(workDir, "one.txt"), "one").ConfigureAwait(false);

        var activeSwarms = this.app.Services.GetRequiredService<ConcurrentDictionary<Guid, SwarmExecution>>();
        activeSwarms.TryRemove(swarmId, out _).Should().BeTrue();

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts/download-zip", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
    }

    private async Task<Guid> CreateSwarmAsync()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        return await manager.CreateSwarmAsync("test goal", templateKey: null).ConfigureAwait(false);
    }

    private string GetWorkDirectory(Guid swarmId)
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var execution = manager.GetSwarm(swarmId);
        execution.Should().NotBeNull();
        return execution!.WorkDirectory;
    }
}
