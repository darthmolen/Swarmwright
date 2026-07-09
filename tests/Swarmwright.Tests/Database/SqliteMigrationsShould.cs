using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Swarmwright.Database;
using Swarmwright.Database.Models;

namespace Swarmwright.Tests.Database;

/// <summary>
/// Verifies the first-class SQLite migration set (in <c>Swarmwright.Database.Sqlite</c>) applies
/// cleanly to a real SQLite database and supports basic CRUD — confirming the provider-conditional
/// model mapping (no jsonb/xmin on SQLite) produces a valid relational schema.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class SqliteMigrationsShould
{
    /// <summary>
    /// Applies the SQLite InitialCreate migration to a fresh file database, inserts a swarm, and
    /// reads it back in a new context.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task ApplyAndSupportCrud()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"swarmwright-sqlite-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseSqlite(
                $"Data Source={dbFile}",
                sqlite => sqlite.MigrationsAssembly("Swarmwright.Database.Sqlite"))
            .Options;

        try
        {
            var swarmId = Guid.NewGuid();

            await using (var context = new SwarmDbContext(options))
            {
                await context.Database.MigrateAsync();

                context.Swarms.Add(new SwarmEntity
                {
                    Id = swarmId,
                    Goal = "sqlite migration test",
                    State = "Created",
                    ContextJson = "{}",
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new SwarmDbContext(options))
            {
                var loaded = await context.Swarms.FindAsync(swarmId);
                loaded.Should().NotBeNull("the SQLite schema created by the migration must persist the swarm row.");
                loaded!.Goal.Should().Be("sqlite migration test");
            }
        }
        finally
        {
            // SQLite keeps the file open via the pool; clear it before deleting.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbFile))
            {
                File.Delete(dbFile);
            }
        }
    }
}
