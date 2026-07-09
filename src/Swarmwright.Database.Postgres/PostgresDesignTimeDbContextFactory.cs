using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Swarmwright.Database.Postgres;

/// <summary>
/// Design-time factory that configures <see cref="SwarmDbContext"/> against the PostgreSQL provider
/// with this assembly as the migrations assembly, so EF Core tooling generates and applies the
/// Npgsql migration set here. The connection string is a placeholder used only at design time.
/// </summary>
public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SwarmDbContext>
{
    /// <summary>
    /// Creates a <see cref="SwarmDbContext"/> for design-time operations.
    /// </summary>
    /// <param name="args">The command-line arguments (unused).</param>
    /// <returns>A configured <see cref="SwarmDbContext"/>.</returns>
    public SwarmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SwarmDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=swarm_design;Username=postgres;Password=postgres",
            npgsql => npgsql.MigrationsAssembly("Swarmwright.Database.Postgres"));
        return new SwarmDbContext(optionsBuilder.Options);
    }
}
