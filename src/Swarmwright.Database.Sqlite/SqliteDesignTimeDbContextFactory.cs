using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Swarmwright.Database.Sqlite;

/// <summary>
/// Design-time factory that configures <see cref="SwarmDbContext"/> against the SQLite provider with
/// this assembly as the migrations assembly, so EF Core tooling generates and applies the SQLite
/// migration set here. The data source is a placeholder used only at design time.
/// </summary>
public sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SwarmDbContext>
{
    /// <summary>
    /// Creates a <see cref="SwarmDbContext"/> for design-time operations.
    /// </summary>
    /// <param name="args">The command-line arguments (unused).</param>
    /// <returns>A configured <see cref="SwarmDbContext"/>.</returns>
    public SwarmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SwarmDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=swarm_design.db",
            sqlite => sqlite.MigrationsAssembly("Swarmwright.Database.Sqlite"));
        return new SwarmDbContext(optionsBuilder.Options);
    }
}
