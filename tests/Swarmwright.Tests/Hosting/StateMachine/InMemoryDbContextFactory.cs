using Swarmwright.Database;
using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Tests.Hosting.StateMachine;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> implementation backed
/// by EF Core's InMemory provider. Every call yields a new context sharing
/// the same in-memory database named by <c>databaseName</c>.
/// </summary>
internal sealed class InMemoryDbContextFactory : IDbContextFactory<SwarmDbContext>
{
    private readonly DbContextOptions<SwarmDbContext> options;

    public InMemoryDbContextFactory(string databaseName)
    {
        this.options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
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
