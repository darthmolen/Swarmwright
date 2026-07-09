using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Integration tests for the augmented swarm list endpoint at <c>GET /api/swarm/</c>.
/// Uses an in-process <see cref="TestServer"/> wired through <see cref="SwarmServiceExtensions.AddSwarmDomain"/>.
/// </summary>
[TestClass]
public sealed partial class SwarmListEndpointTests
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
        this.workBasePath = Path.Combine(Path.GetTempPath(), "swarm-list-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workBasePath);

        this.app = BuildApp(this.workBasePath, configureOverrides: null);
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
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Verifies the augmented list endpoint surfaces an active swarm with all new fields.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_WithActiveOnlyInMemory_ReturnsDtoShape()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var swarmId = await manager.CreateSwarmAsync("goal-active-only", templateKey: null).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetArrayLength().Should().BeGreaterThan(0);

        var match = root.EnumerateArray().First(e => e.GetProperty("swarmId").GetGuid() == swarmId);
        match.GetProperty("goal").GetString().Should().Be("goal-active-only");
        match.GetProperty("phase").GetString().Should().NotBeNullOrEmpty();
        match.GetProperty("createdAt").GetString().Should().NotBeNullOrEmpty();

        // The task/worker counts are dispatcher-timing-dependent: the dispatcher
        // may persist task rows during CreateSwarmAsync before this GET runs.
        // We only assert the fields are present with non-negative integers. The
        // count-projection contract is validated at the repository level by
        // SwarmListRepositoryTests.ListAllSwarmsAsync_IncludesTaskCount et al.
        match.GetProperty("taskCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        match.GetProperty("workerCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);

        // `isRunning` must be present; the dispatcher may or may not have
        // advanced this execution past the running state by the time the
        // endpoint observes it, so we do not assert the exact boolean value.
        match.TryGetProperty("isRunning", out var running).Should().BeTrue();
        (running.ValueKind == JsonValueKind.True || running.ValueKind == JsonValueKind.False).Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a swarm stored only in the database appears in the list with
    /// <c>isRunning = false</c> and a populated <c>completedAt</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_WithCompletedOnlyInDb_ReturnsFromDatabase()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "db-only-goal",
            State = "Complete",
            TemplateKey = "deep-research",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        }).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var match = doc.RootElement.EnumerateArray().FirstOrDefault(e => e.GetProperty("swarmId").GetGuid() == swarmId);
        match.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        match.GetProperty("goal").GetString().Should().Be("db-only-goal");
        match.GetProperty("isRunning").GetBoolean().Should().BeFalse();
        match.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    /// <summary>
    /// Verifies that when a swarm appears in both in-memory and database sources the
    /// merged entry fills count fields (<c>taskCount</c>, <c>workerCount</c>) from the
    /// database row — in-memory executions do not carry those fields. The broader
    /// "in-memory wins on live-state" contract (phase, isRunning) is validated at the
    /// unit-test layer by <c>SwarmListMergerTests.Merge_SameIdInBoth_UsesInMemoryPhaseAndDbCounts</c>;
    /// at the endpoint layer the dispatcher-no-<c>IChatClient</c> race makes the
    /// live-state assertions unreliable.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_WhenSwarmInBothSources_DbCountsFillActiveEntry()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var swarmId = await manager.CreateSwarmAsync("both-sources", templateKey: null).ConfigureAwait(false);

        // Seed DB with the same id but a stale "Executing" phase and high counts.
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "both-sources",
            State = "Executing",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        }).ConfigureAwait(false);
        await this.SeedTasksAsync(swarmId, count: 12).ConfigureAwait(false);
        await this.SeedAgentsAsync(swarmId, count: 4).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var matches = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("swarmId").GetGuid() == swarmId)
            .ToList();
        matches.Should().ContainSingle();

        var match = matches[0];

        // The in-memory source wins for live-state fields; the DB source fills in
        // counts. The active swarm's isRunning value is dispatcher-dependent in
        // this test host (no IChatClient), so we only assert the DB-sourced counts
        // here. The merger preference for in-memory live-state is validated by
        // SwarmListMergerTests.Merge_SameIdInBoth_UsesInMemoryPhaseAndDbCounts.
        match.GetProperty("taskCount").GetInt32().Should().Be(12);
        match.GetProperty("workerCount").GetInt32().Should().Be(4);
    }

    /// <summary>
    /// Verifies that the list is ordered by activity descending (newest first).
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_OrdersByLastEventDescending()
    {
        var now = DateTime.UtcNow;
        var oldestId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = oldestId,
            Goal = "oldest-db",
            State = "Complete",
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddHours(-2),
            CompletedAt = now.AddHours(-2),
        }).ConfigureAwait(false);
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = newestId,
            Goal = "newest-db",
            State = "Complete",
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddMinutes(-5),
            CompletedAt = now.AddMinutes(-5),
        }).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("swarmId").GetGuid())
            .ToList();

        var idxNewest = ids.IndexOf(newestId);
        var idxOldest = ids.IndexOf(oldestId);
        idxNewest.Should().BeGreaterThanOrEqualTo(0);
        idxOldest.Should().BeGreaterThanOrEqualTo(0);
        idxNewest.Should().BeLessThan(idxOldest);
    }

    /// <summary>
    /// Verifies that the limit query parameter caps the historical slice of results.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_RespectsLimitQueryParameter()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await this.SeedSwarmAsync(new SwarmEntity
            {
                Id = Guid.NewGuid(),
                Goal = $"seed-{i}",
                State = "Complete",
                CreatedAt = now.AddMinutes(-i - 1),
                UpdatedAt = now.AddMinutes(-i - 1),
                CompletedAt = now.AddMinutes(-i - 1),
            }).ConfigureAwait(false);
        }

        var response = await this.client.GetAsync(new Uri("/api/swarm/?limit=2", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    /// <summary>
    /// Verifies that the since query parameter excludes rows older than the cutoff.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_RespectsSinceQueryParameter()
    {
        var now = DateTime.UtcNow;
        var recentId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = recentId,
            Goal = "recent",
            State = "Complete",
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
            CompletedAt = now.AddMinutes(-5),
        }).ConfigureAwait(false);
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = staleId,
            Goal = "stale",
            State = "Complete",
            CreatedAt = now.AddHours(-3),
            UpdatedAt = now.AddHours(-3),
            CompletedAt = now.AddHours(-3),
        }).ConfigureAwait(false);

        var since = now.AddHours(-1).ToString("o");
        var response = await this.client.GetAsync(new Uri($"/api/swarm/?since={Uri.EscapeDataString(since)}", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("swarmId").GetGuid())
            .ToList();

        ids.Should().Contain(recentId);
        ids.Should().NotContain(staleId);
    }

    /// <summary>
    /// Verifies that the response still exposes the original five fields with the same
    /// names and types. Regression guard for existing frontend callers.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_BackwardsCompatible_StillReturnsOriginalFields()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        await manager.CreateSwarmAsync("regression-goal", templateKey: "deep-research").ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();

        first.TryGetProperty("swarmId", out var swarmIdProp).Should().BeTrue();
        swarmIdProp.ValueKind.Should().Be(JsonValueKind.String);
        first.TryGetProperty("goal", out var goalProp).Should().BeTrue();
        goalProp.ValueKind.Should().Be(JsonValueKind.String);
        first.TryGetProperty("templateKey", out _).Should().BeTrue();
        first.TryGetProperty("phase", out var phaseProp).Should().BeTrue();
        phaseProp.ValueKind.Should().Be(JsonValueKind.String);
        first.TryGetProperty("isRunning", out var runningProp).Should().BeTrue();
        (runningProp.ValueKind == JsonValueKind.True || runningProp.ValueKind == JsonValueKind.False).Should().BeTrue();
    }

    /// <summary>
    /// Verifies that the response JSON uses camelCase property names. Parses raw JSON
    /// directly so the case-insensitive typed deserializer cannot mask a PascalCase regression.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_ResponseJson_UsesCamelCaseKeys()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        await manager.CreateSwarmAsync("casing", templateKey: null).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();

        var expectedKeys = new[]
        {
            "swarmId", "goal", "templateKey", "phase", "isRunning",
            "createdAt", "completedAt", "lastEventAt", "taskCount", "workerCount",
        };

        var actualKeys = first.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var key in expectedKeys)
        {
            actualKeys.Should().Contain(key);
        }
    }

    /// <summary>
    /// Verifies that the <c>createdAt</c> field serializes as an ISO 8601 UTC string
    /// with the trailing <c>Z</c> suffix.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_CreatedAtField_IsUtcIso8601WithZSuffix()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        await manager.CreateSwarmAsync("utc-suffix", templateKey: null).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();
        var createdAt = first.GetProperty("createdAt").GetString();
        createdAt.Should().NotBeNullOrEmpty();
        Iso8601UtcRegex().IsMatch(createdAt!).Should().BeTrue();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}.*Z$")]
    private static partial Regex Iso8601UtcRegex();

    /// <summary>
    /// Verifies the repository-unavailable fallback path: when the repository throws,
    /// the endpoint still returns 200 with active-only results.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmList_WhenRepositoryThrows_FallsBackToActiveList()
    {
        // Tear down the default host and stand up a fresh one with a mocked repository.
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
        this.client.Dispose();

        var mockRepo = new Mock<ISwarmRepository>();
        mockRepo
            .Setup(r => r.ListAllSwarmsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db not configured"));

        this.app = BuildApp(
            this.workBasePath,
            configureOverrides: services =>
            {
                // Replace the registered ISwarmRepository with a mock that throws.
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISwarmRepository));
                if (descriptor is not null)
                {
                    services.Remove(descriptor);
                }

                services.AddScoped<ISwarmRepository>(_ => mockRepo.Object);
            });
        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();

        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var swarmId = await manager.CreateSwarmAsync("survives-failure", templateKey: null).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri("/api/swarm/", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("swarmId").GetGuid())
            .ToList();
        ids.Should().Contain(swarmId);
    }

    /// <summary>
    /// Verifies the events endpoint honors the <c>?limit=</c> query parameter and
    /// returns the NEWEST N events (not the oldest). The returned payload is still
    /// ordered chronologically ascending so downstream consumers can replay the
    /// slice through the reducer without re-sorting.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetEvents_RespectsLimitQueryParameter()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "events-limit",
            State = "Complete",
        }).ConfigureAwait(false);

        // Seed 10 events for this swarm with strictly increasing timestamps.
        using (var scope = this.app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
            var baseTime = DateTime.UtcNow;
            for (var i = 0; i < 10; i++)
            {
                ctx.Events.Add(new EventEntity
                {
                    SwarmId = swarmId,
                    EventType = $"evt-{i}",
                    CreatedAt = baseTime.AddSeconds(i),
                });
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/events?limit=3", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetArrayLength().Should().Be(3);

        // NEWEST-first semantics: the three returned events must be evt-7, evt-8, evt-9,
        // in chronological order. Users want to backfill recent timeline context when
        // connecting to an existing swarm, not the earliest history.
        var eventTypes = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();
        eventTypes.Should().Equal("evt-7", "evt-8", "evt-9");
    }

    /// <summary>
    /// Regression guard for NEWEST-first ordering under a large event set. Seeds 150
    /// events with increasing timestamps, asks for the newest 100, and asserts the
    /// returned slice is events 50..149 in chronological order.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetEvents_RespectsLimitOrderingNewestFirst()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "events-newest-first",
            State = "Complete",
        }).ConfigureAwait(false);

        using (var scope = this.app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
            var baseTime = DateTime.UtcNow.AddMinutes(-10);
            for (var i = 0; i < 150; i++)
            {
                ctx.Events.Add(new EventEntity
                {
                    SwarmId = swarmId,
                    EventType = $"evt-{i:D3}",
                    CreatedAt = baseTime.AddMilliseconds(i),
                });
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/events?limit=100", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetArrayLength().Should().Be(100);

        var eventTypes = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();
        eventTypes.First().Should().Be("evt-050");
        eventTypes.Last().Should().Be("evt-149");

        // Chronological order preserved across the slice.
        var expected = Enumerable.Range(50, 100).Select(i => $"evt-{i:D3}").ToList();
        eventTypes.Should().Equal(expected);
    }

    /// <summary>
    /// Verifies the <c>GET /api/swarm/{id}</c> DB fallback: a swarm that exists
    /// only in the database (because the dispatcher already evicted it from the
    /// in-memory manager) is still hydratable via the metadata endpoint. This
    /// is the load-bearing behavior for Tab B's ReportList click path.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmMetadata_WhenOnlyInDatabase_ReturnsFromDb()
    {
        var swarmId = Guid.NewGuid();
        var created = DateTime.UtcNow.AddMinutes(-30);
        var completed = DateTime.UtcNow.AddMinutes(-10);
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "db-only-metadata",
            State = "Complete",
            TemplateKey = "deep-research",
            CreatedAt = created,
            UpdatedAt = completed,
            CompletedAt = completed,
        }).ConfigureAwait(false);

        var response = await this.client
            .GetAsync(new Uri($"/api/swarm/{swarmId}", UriKind.Relative))
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("swarmId").GetGuid().Should().Be(swarmId);
        root.GetProperty("goal").GetString().Should().Be("db-only-metadata");
        root.GetProperty("templateKey").GetString().Should().Be("deep-research");
        root.GetProperty("phase").GetString().Should().Be("Complete");
        root.GetProperty("isRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    /// <summary>
    /// Verifies that when the repository throws (for example, a deployment
    /// without a configured database) and the swarm is not in memory, the
    /// endpoint preserves the pre-fallback behavior by returning 404 instead
    /// of propagating the exception.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmMetadata_WhenDbThrows_Returns404()
    {
        // Tear down the default host and stand up a fresh one with a mocked repository.
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
        this.client.Dispose();

        var mockRepo = new Mock<ISwarmRepository>();
        mockRepo
            .Setup(r => r.GetSwarmAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new InvalidOperationException("db not configured"));
        mockRepo
            .Setup(r => r.ListAllSwarmsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SwarmListEntry>());

        this.app = BuildApp(
            this.workBasePath,
            configureOverrides: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISwarmRepository));
                if (descriptor is not null)
                {
                    services.Remove(descriptor);
                }

                services.AddScoped<ISwarmRepository>(_ => mockRepo.Object);
            });
        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();

        // Use an id the in-memory manager definitely does not own.
        var unknownId = Guid.NewGuid();
        var response = await this.client
            .GetAsync(new Uri($"/api/swarm/{unknownId}", UriKind.Relative))
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies that when a swarm exists in both the in-memory manager and the
    /// database, the in-memory path wins — the handler does not fall through
    /// to the DB lookup. We assert on <c>goal</c> because it is the only field
    /// whose value is distinct enough between the two sources to detect which
    /// branch served the response; <c>isRunning</c> is dispatcher-timing
    /// dependent in this no-<c>IChatClient</c> test host and cannot be used.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetSwarmMetadata_WhenInMemory_StillPrefersInMemory()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var swarmId = await manager.CreateSwarmAsync("in-memory-goal", templateKey: null).ConfigureAwait(false);

        // Seed a stale DB row with the same id but a different goal. If the
        // handler falls through to the repository path, the response will
        // carry "stale-db-goal" and this test will fail.
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "stale-db-goal",
            State = "Executing",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
        }).ConfigureAwait(false);

        var response = await this.client
            .GetAsync(new Uri($"/api/swarm/{swarmId}", UriKind.Relative))
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("swarmId").GetGuid().Should().Be(swarmId);
        root.GetProperty("goal").GetString().Should().Be("in-memory-goal");
    }

    /// <summary>
    /// Verifies that <c>POST /api/swarm/</c> returns camelCase keys in the response JSON.
    /// Regression guard: the endpoint must use <c>Results.Json</c> with <c>SwarmJsonOptions.Default</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task CreateSwarm_ResponseJson_UsesCamelCaseKeys()
    {
        using var content = new StringContent(
            """{"goal":"test","templateKey":null}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await this.client.PostAsync(new Uri("/api/swarm/", UriKind.Relative), content).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("swarmId", out _).Should().BeTrue("response should contain camelCase key 'swarmId'");
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/tasks</c> returns camelCase keys in the response JSON.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetTasks_ResponseJson_UsesCamelCaseKeys()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "task-casing",
            State = "Executing",
        }).ConfigureAwait(false);
        await this.SeedTasksAsync(swarmId, count: 1).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/tasks", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();
        first.TryGetProperty("swarmId", out _).Should().BeTrue("TaskEntity.SwarmId should serialize as camelCase 'swarmId'");
        first.TryGetProperty("subject", out _).Should().BeTrue("TaskEntity.Subject should serialize as camelCase 'subject'");
        first.TryGetProperty("workerRole", out _).Should().BeTrue("TaskEntity.WorkerRole should serialize as camelCase 'workerRole'");
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/agents</c> returns camelCase keys in the response JSON.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetAgents_ResponseJson_UsesCamelCaseKeys()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "agent-casing",
            State = "Executing",
        }).ConfigureAwait(false);
        await this.SeedAgentsAsync(swarmId, count: 1).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/agents", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();
        first.TryGetProperty("swarmId", out _).Should().BeTrue("AgentEntity.SwarmId should serialize as camelCase 'swarmId'");
        first.TryGetProperty("displayName", out _).Should().BeTrue("AgentEntity.DisplayName should serialize as camelCase 'displayName'");
        first.TryGetProperty("tasksCompleted", out _).Should().BeTrue("AgentEntity.TasksCompleted should serialize as camelCase 'tasksCompleted'");
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/events</c> returns camelCase keys in the response JSON.
    /// The <c>eventType</c> key must not appear as PascalCase <c>EventType</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetEvents_ResponseJson_UsesCamelCaseKeys()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "event-casing",
            State = "Complete",
        }).ConfigureAwait(false);

        using (var scope = this.app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
            ctx.Events.Add(new EventEntity
            {
                SwarmId = swarmId,
                EventType = "swarm.created",
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/events", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();
        first.TryGetProperty("eventType", out _).Should().BeTrue("EventEntity.EventType should serialize as camelCase 'eventType'");
        first.TryGetProperty("EventType", out _).Should().BeFalse("PascalCase 'EventType' key should not appear");
        first.TryGetProperty("dataJson", out _).Should().BeTrue("EventEntity.DataJson should serialize as camelCase 'dataJson'");
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/templates</c> returns camelCase keys in the response JSON.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetTemplates_ResponseJson_UsesCamelCaseKeys()
    {
        var response = await this.client.GetAsync(new Uri("/api/swarm/templates", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        // Templates may be empty if no template directory is configured, but the
        // response shape (an array) must still be valid JSON. If templates exist,
        // assert camelCase keys.
        if (doc.RootElement.GetArrayLength() > 0)
        {
            var first = doc.RootElement.EnumerateArray().First();
            first.TryGetProperty("key", out _).Should().BeTrue("template key should serialize as camelCase 'key'");
            first.TryGetProperty("name", out _).Should().BeTrue("template name should serialize as camelCase 'name'");
            first.TryGetProperty("description", out _).Should().BeTrue("template description should serialize as camelCase 'description'");
        }
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/artifacts</c> returns camelCase keys in the response JSON.
    /// The <c>files</c> key must be present.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetArtifacts_ResponseJson_UsesCamelCaseKeys()
    {
        var manager = this.app.Services.GetRequiredService<ISwarmManager>();
        var swarmId = await manager.CreateSwarmAsync("artifacts-casing", templateKey: null).ConfigureAwait(false);

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/artifacts", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("files", out _).Should().BeTrue("response should contain camelCase key 'files'");
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/tasks</c> returns the domain
    /// <see cref="Models.SwarmTask"/> shape: <c>status</c> carries the
    /// PascalCase enum name (from <c>TaskEntity.State</c>) and <c>blockedBy</c>
    /// is a parsed array (not the raw <c>blockedByJson</c> string).
    /// Regression guard for the rehydration-drops-state bug: before this
    /// fix, <c>/tasks</c> returned raw <c>TaskEntity</c> rows whose
    /// <c>state</c> key the frontend normalizer treated as <c>undefined</c>,
    /// rendering every rehydrated task as Pending.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetTasks_ResponseJson_UsesSwarmTaskDomainShape()
    {
        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "task-shape",
            State = "Executing",
        }).ConfigureAwait(false);

        using (var scope = this.app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
            ctx.Tasks.Add(new TaskEntity
            {
                SwarmId = swarmId,
                Id = "t0",
                Subject = "s0",
                Description = "d0",
                WorkerRole = "engineer",
                WorkerName = "eng-alpha",
                State = "Completed",
                BlockedByJson = "[\"t1\",\"t2\"]",
            });
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await this.client.GetAsync(new Uri($"/api/swarm/{swarmId}/tasks", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().First();

        first.GetProperty("status").GetString().Should().Be("Completed");
        first.TryGetProperty("state", out _).Should().BeFalse("entity 'state' column should not leak through as a top-level key");

        var blockedBy = first.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToList();
        blockedBy.Should().Equal("t1", "t2");
        first.TryGetProperty("blockedByJson", out _).Should().BeFalse("entity 'blockedByJson' column should not leak through");

        first.GetProperty("swarmId").GetGuid().Should().Be(swarmId);
    }

    /// <summary>
    /// Verifies the <c>POST /api/swarm/{id}/mark-as-awaiting-intervention</c>
    /// endpoint (Manual Recover): a DB-persisted swarm in <c>Failed</c>
    /// state transitions to <c>AwaitingIntervention</c> via the
    /// <see cref="ISwarmInterventionHandler"/>. End-to-end check that the
    /// route binding, write-policy gating, and state-machine wiring agree.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task PostMarkAsAwaitingIntervention_OnFailedSwarm_Returns204AndTransitionsToAwaitingIntervention()
    {
        // The intervention handler depends transitively on IChatClient (via
        // DefaultLeaderRepairAdvisor). Stand up a fresh host with a stub so
        // minimal-API activation succeeds on the mark-as-awaiting-intervention
        // route.
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
        this.client.Dispose();

        this.app = BuildApp(
            this.workBasePath,
            configureOverrides: services =>
            {
                services.AddSingleton<IChatClient>(_ => Mock.Of<IChatClient>());
            });
        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();

        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "failed-swarm",
            State = nameof(SwarmInstanceState.Failed),
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);

        var response = await this.client
            .PostAsync(new Uri($"/api/swarm/{swarmId}/mark-as-awaiting-intervention", UriKind.Relative), content: null)
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = this.app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var swarm = await ctx.Swarms.FindAsync(swarmId).ConfigureAwait(false);
        swarm!.State.Should().Be(nameof(SwarmInstanceState.AwaitingIntervention));
    }

    /// <summary>
    /// Verifies that <c>GET /api/swarm/{id}/stream</c> returns 404 when the
    /// swarm is not in memory AND does not exist in the repository. The
    /// endpoint's first action is <see cref="ISwarmManager.EnsureLiveAsync"/>;
    /// when that returns null, the endpoint falls through to the null-check
    /// and emits 404. Behavioral complement to
    /// <see cref="GetStream_WhenSwarmOnlyInDb_RehydratesAndStreams"/> which
    /// covers the positive path.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetStream_WhenSwarmUnknown_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var response = await this.client
            .GetAsync(new Uri($"/api/swarm/{unknownId}/stream", UriKind.Relative))
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies the full rehydrate-and-stream path: when a non-terminal swarm
    /// exists only in the database (evicted from the in-memory manager), the
    /// stream endpoint uses <see cref="ISwarmManager.EnsureLiveAsync"/> to
    /// wake it up and begins an SSE stream (status 200, not 404).
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task GetStream_WhenSwarmOnlyInDb_RehydratesAndStreams()
    {
        // Tear down the default host and stand up a fresh one with a stubbed
        // IChatClient — the orchestrator factory requires one to build the
        // rehydrated execution, and AddSwarmDomain does not register it.
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
        this.client.Dispose();

        this.app = BuildApp(
            this.workBasePath,
            configureOverrides: services =>
            {
                services.AddSingleton<IChatClient>(_ => Mock.Of<IChatClient>());
            });
        await this.app.StartAsync().ConfigureAwait(false);
        this.client = this.app.GetTestClient();

        var swarmId = Guid.NewGuid();
        await this.SeedSwarmAsync(new SwarmEntity
        {
            Id = swarmId,
            Goal = "stream-rehydrate",
            State = "AwaitingIntervention",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);

        // Use ResponseHeadersRead so we observe the status line before the
        // SSE loop's first heartbeat. Cancel aggressively to avoid hanging.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await this.client.GetAsync(
            new Uri($"/api/swarm/{swarmId}/stream", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }

    private static WebApplication BuildApp(string workBasePath, Action<IServiceCollection>? configureOverrides)
    {
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
                ["Swarm:WorkBasePath"] = workBasePath,
            })
            .Build();

        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddSwarmDomain(configuration, builder.Environment);
        builder.Services.AddSwarmHttpServices();

        configureOverrides?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapSwarmEndpoints(useSwarmPolicies: false);
        return app;
    }

    private async Task SeedSwarmAsync(SwarmEntity swarm)
    {
        using var scope = this.app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedTasksAsync(Guid swarmId, int count)
    {
        using var scope = this.app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            ctx.Tasks.Add(new TaskEntity
            {
                SwarmId = swarmId,
                Id = $"t{i}",
                Subject = $"s{i}",
                Description = $"d{i}",
            });
        }

        await ctx.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedAgentsAsync(Guid swarmId, int count)
    {
        using var scope = this.app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SwarmDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            ctx.Agents.Add(new AgentEntity
            {
                SwarmId = swarmId,
                Name = $"w{i}",
                Role = "worker",
                DisplayName = $"W{i}",
            });
        }

        await ctx.SaveChangesAsync().ConfigureAwait(false);
    }
}
