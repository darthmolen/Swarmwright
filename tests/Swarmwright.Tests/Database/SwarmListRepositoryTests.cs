using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Tests.Database;

/// <summary>
/// Tests for <see cref="SwarmRepository.ListAllSwarmsAsync"/>, which merges
/// <see cref="SwarmEntity"/> rows with per-swarm task, agent, and event aggregates.
/// </summary>
[TestClass]
public sealed class SwarmListRepositoryTests
{
    private static (SwarmDbContext Context, SwarmRepository Repo) CreateSut()
    {
        var options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseInMemoryDatabase("SwarmListTest_" + Guid.NewGuid())
            .Options;
        var ctx = new SwarmDbContext(options);
        var factory = new InMemoryDbContextFactory(options);
        var repo = new SwarmRepository(factory);
        return (ctx, repo);
    }

    /// <summary>
    /// Verifies that listing an empty database returns an empty result set.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_EmptyDatabase_ReturnsEmpty()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Verifies that swarms are returned ordered by last activity descending.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_ReturnsSwarmsOrderedByLastActivity()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var now = DateTime.UtcNow;
            var s1 = new SwarmEntity { Id = Guid.NewGuid(), Goal = "oldest", CreatedAt = now.AddMinutes(-30), UpdatedAt = now.AddMinutes(-30) };
            var s2 = new SwarmEntity { Id = Guid.NewGuid(), Goal = "middle", CreatedAt = now.AddMinutes(-20), UpdatedAt = now.AddMinutes(-20) };
            var s3 = new SwarmEntity { Id = Guid.NewGuid(), Goal = "newest", CreatedAt = now.AddMinutes(-10), UpdatedAt = now.AddMinutes(-10) };
            ctx.Swarms.AddRange(s1, s2, s3);
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().HaveCount(3);
            result[0].Goal.Should().Be("newest");
            result[1].Goal.Should().Be("middle");
            result[2].Goal.Should().Be("oldest");
        }
    }

    /// <summary>
    /// Verifies that the returned entry carries the task count for each swarm.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_IncludesTaskCount()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity { Id = Guid.NewGuid(), Goal = "with-tasks" };
            ctx.Swarms.Add(swarm);
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarm.Id, Id = "t1", Subject = "a", Description = "a" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarm.Id, Id = "t2", Subject = "b", Description = "b" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarm.Id, Id = "t3", Subject = "c", Description = "c" });
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().ContainSingle();
            result[0].TaskCount.Should().Be(3);
        }
    }

    /// <summary>
    /// Verifies that the returned entry carries the worker (agent) count for each swarm.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_IncludesWorkerCount()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity { Id = Guid.NewGuid(), Goal = "with-agents" };
            ctx.Swarms.Add(swarm);
            ctx.Agents.Add(new AgentEntity { SwarmId = swarm.Id, Name = "w1", Role = "r", DisplayName = "W1" });
            ctx.Agents.Add(new AgentEntity { SwarmId = swarm.Id, Name = "w2", Role = "r", DisplayName = "W2" });
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().ContainSingle();
            result[0].WorkerCount.Should().Be(2);
        }
    }

    /// <summary>
    /// Verifies that the returned entry carries the last event timestamp when events exist.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_IncludesLastEventTimestamp()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            var baseTime = DateTime.UtcNow.AddMinutes(-5);
            var latest = baseTime.AddMinutes(4);
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "events" });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "a", CreatedAt = baseTime });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "b", CreatedAt = latest });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "c", CreatedAt = baseTime.AddMinutes(2) });
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().ContainSingle();
            result[0].LastEventAt.Should().NotBeNull();
            result[0].LastEventAt!.Value.Should().BeCloseTo(latest, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Verifies that the limit parameter caps the number of returned rows to the most recent.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_RespectsLimit()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 10; i++)
            {
                ctx.Swarms.Add(new SwarmEntity
                {
                    Id = Guid.NewGuid(),
                    Goal = $"swarm-{i}",
                    CreatedAt = now.AddMinutes(-i),
                    UpdatedAt = now.AddMinutes(-i),
                });
            }

            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(3, since: null, CancellationToken.None);
            result.Should().HaveCount(3);

            // Most-recent three are i=0,1,2 by UpdatedAt descending.
            result.Select(r => r.Goal).Should().Equal("swarm-0", "swarm-1", "swarm-2");
        }
    }

    /// <summary>
    /// Verifies that the since filter excludes swarms older than the cutoff.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_RespectsSinceFilter()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
            {
                ctx.Swarms.Add(new SwarmEntity
                {
                    Id = Guid.NewGuid(),
                    Goal = $"swarm-{i}",
                    CreatedAt = now.AddMinutes(-i * 10),
                    UpdatedAt = now.AddMinutes(-i * 10),
                });
            }

            await ctx.SaveChangesAsync();

            // Since cutoff = 15 minutes ago. Only swarms updated within last 15
            // minutes (i=0, i=1) should be returned.
            var since = now.AddMinutes(-15);
            var result = await repo.ListAllSwarmsAsync(50, since: since, CancellationToken.None);
            result.Should().HaveCount(2);
            var goals = result.Select(r => r.Goal).ToList();
            goals.Should().Contain("swarm-0");
            goals.Should().Contain("swarm-1");
        }
    }

    /// <summary>
    /// Verifies that a swarm with zero events exposes a last-event timestamp equal to its updated-at.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_NoEvents_LastEventAtFallsBackToUpdatedAt()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity
            {
                Id = Guid.NewGuid(),
                Goal = "no-events",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-7),
            };
            ctx.Swarms.Add(swarm);
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().ContainSingle();
            result[0].LastEventAt.Should().NotBeNull();
            result[0].LastEventAt!.Value.Should().BeCloseTo(swarm.UpdatedAt, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Verifies that every <see cref="DateTime"/> field on loaded entries has
    /// <see cref="DateTimeKind.Utc"/>. SQLite strips <see cref="DateTime.Kind"/>
    /// on load, so the repository must project values through
    /// <see cref="DateTime.SpecifyKind(DateTime, DateTimeKind)"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ListAllSwarmsAsync_LoadedDateTimes_KindIsUtc()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            var swarm = new SwarmEntity
            {
                Id = swarmId,
                Goal = "utc-kind",
                CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-3), DateTimeKind.Unspecified),
                UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-2), DateTimeKind.Unspecified),
                CompletedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-1), DateTimeKind.Unspecified),
            };
            ctx.Swarms.Add(swarm);
            ctx.Events.Add(new EventEntity
            {
                SwarmId = swarmId,
                EventType = "any",
                CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-1), DateTimeKind.Unspecified),
            });
            await ctx.SaveChangesAsync();

            var result = await repo.ListAllSwarmsAsync(50, since: null, CancellationToken.None);
            result.Should().ContainSingle();

            var entry = result[0];
            entry.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            entry.CompletedAt.Should().NotBeNull();
            entry.CompletedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
            entry.LastEventAt.Should().NotBeNull();
            entry.LastEventAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> used by these unit tests. Each
    /// call returns a brand-new <see cref="SwarmDbContext"/> bound to the same shared
    /// InMemory database (keyed by a per-test Guid in <see cref="CreateSut"/>) so that
    /// rows persisted by the repository remain visible to the assertion-side context.
    /// </summary>
    private sealed class InMemoryDbContextFactory : IDbContextFactory<SwarmDbContext>
    {
        private readonly DbContextOptions<SwarmDbContext> options;

        public InMemoryDbContextFactory(DbContextOptions<SwarmDbContext> options)
        {
            this.options = options;
        }

        public SwarmDbContext CreateDbContext()
        {
            return new SwarmDbContext(this.options);
        }

        public Task<SwarmDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SwarmDbContext(this.options));
        }
    }
}
