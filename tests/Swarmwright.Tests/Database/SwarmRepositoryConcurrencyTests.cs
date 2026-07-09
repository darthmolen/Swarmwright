using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Tests.Database;

/// <summary>
/// Concurrency regression tests for the <see cref="SwarmRepository"/> class.
/// </summary>
/// <remarks>
/// These tests run against a real Sqlite provider (rather than the EF Core InMemory
/// provider) because the InMemory provider does not enforce the single-operation contract
/// that EF Core's <c>ConcurrencyDetector</c> protects against. A real relational provider
/// is required to demonstrate the <c>InvalidOperationException: A second operation was
/// started on this context instance</c> that Bug E produces when multiple workers share a
/// single <see cref="SwarmDbContext"/>.
/// <para>
/// The tests use a short-lived temporary file-based Sqlite database (rather than an
/// in-memory one) so that each <see cref="SwarmDbContext"/> instance opens its own
/// independent connection. An in-memory Sqlite database requires a shared connection,
/// which serialises writes in a way that masks the concurrency question we care about and
/// introduces unrelated lock contention.
/// </para>
/// </remarks>
[TestClass]
public sealed class SwarmRepositoryConcurrencyTests : IDisposable
{
    private string dbPath = null!;
    private string connectionString = null!;
    private DbContextOptions<SwarmDbContext> options = null!;
    private TestDbContextFactory factory = null!;

    /// <summary>
    /// Initializes a fresh file-based Sqlite database for each test.
    /// </summary>
    /// <returns>A task representing the asynchronous initialization.</returns>
    [TestInitialize]
    public async Task TestInitializeAsync()
    {
        this.dbPath = Path.Combine(Path.GetTempPath(), $"swarm-concurrency-{Guid.NewGuid():N}.db");
        this.connectionString = $"DataSource={this.dbPath}";

        this.options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseSqlite(this.connectionString)
            .Options;

        await using (var ctx = new SwarmDbContext(this.options))
        {
            await ctx.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        this.factory = new TestDbContextFactory(this.options);
    }

    /// <summary>
    /// Deletes the temporary Sqlite database file after each test.
    /// </summary>
    [TestCleanup]
    public void TestCleanup()
    {
        SqliteConnection.ClearAllPools();
        if (!string.IsNullOrEmpty(this.dbPath) && File.Exists(this.dbPath))
        {
            try
            {
                File.Delete(this.dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup — the OS may still hold the file briefly.
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.TestCleanup();
    }

    /// <summary>
    /// Verifies that multiple concurrent reads on the same repository instance
    /// do not throw <c>InvalidOperationException</c> from EF Core's
    /// <c>ConcurrencyDetector</c>. Previously this test exercised
    /// <c>UpdateTaskStatusAsync</c>; that method was removed in Phase B5
    /// but the underlying property — per-call <see cref="IDbContextFactory{TContext}"/>
    /// contexts safely serve concurrent callers — still needs a regression test.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [TestMethod]
    public async Task Repository_WhenCalledConcurrentlyFromMultipleThreads_DoesNotThrow()
    {
        // Arrange — seed a swarm and ten tasks.
        var repository = new SwarmRepository(this.factory);
        var swarmId = Guid.NewGuid();

        await using (var ctx = await this.factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "concurrency test" });
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var taskIds = Enumerable.Range(0, 10)
            .Select(i => $"task-{i:D2}")
            .ToArray();

        foreach (var id in taskIds)
        {
            await repository.CreateTaskAsync(new TaskEntity
            {
                SwarmId = swarmId,
                Id = id,
                Subject = "Concurrent",
                Description = "Concurrent read test",
            }).ConfigureAwait(false);
        }

        // Act — fire 10 concurrent reads on the same repository instance.
        var concurrentOps = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => repository.GetTasksAsync(swarmId)))
            .ToArray();

        var act = async () => await Task.WhenAll(concurrentOps).ConfigureAwait(false);

        // Assert — no concurrency exception.
        await act.Should().NotThrowAsync().ConfigureAwait(false);

        // Sanity — every task was readable.
        var persisted = await repository.GetTasksAsync(swarmId).ConfigureAwait(false);
        persisted.Should().HaveCount(10);
    }

    /// <summary>
    /// Verifies that concurrent read and write calls on the same repository do not throw
    /// <c>InvalidOperationException</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [TestMethod]
    public async Task Repository_ConcurrentReadAndWrite_DoesNotThrow()
    {
        // Arrange.
        var repository = new SwarmRepository(this.factory);
        var swarmId = Guid.NewGuid();

        await using (var ctx = await this.factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "read/write test" });
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act — one loop reads, one loop writes, until the deadline.
        var reader = Task.Run(
            async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    _ = await repository.GetTasksAsync(swarmId).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        var writer = Task.Run(
            async () =>
            {
                var counter = 0;
                while (!cts.IsCancellationRequested)
                {
                    await repository.CreateTaskAsync(new TaskEntity
                    {
                        SwarmId = swarmId,
                        Id = $"t-{counter++:D6}",
                        Subject = "Write",
                        Description = "d",
                    }).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        var act = () => Task.WhenAll(reader, writer);

        // Assert — completes without exceptions.
        await act.Should().NotThrowAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> implementation that returns a fresh
    /// <see cref="SwarmDbContext"/> (each with its own connection to the shared test database
    /// file) on every call, mirroring the production context factory wiring.
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<SwarmDbContext>
    {
        private readonly DbContextOptions<SwarmDbContext> options;

        public TestDbContextFactory(DbContextOptions<SwarmDbContext> options)
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
