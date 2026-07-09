using Microsoft.EntityFrameworkCore;
using Swarmwright.Database.Models;

namespace Swarmwright.Database;

/// <summary>
/// Database context for swarm orchestration storage.
/// </summary>
public class SwarmDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmDbContext"/> class.
    /// </summary>
    /// <param name="options">The database context options.</param>
    public SwarmDbContext(DbContextOptions<SwarmDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gets the swarm sessions.</summary>
    public DbSet<SwarmEntity> Swarms => this.Set<SwarmEntity>();

    /// <summary>Gets the swarm tasks.</summary>
    public DbSet<TaskEntity> Tasks => this.Set<TaskEntity>();

    /// <summary>Gets the swarm agents.</summary>
    public DbSet<AgentEntity> Agents => this.Set<AgentEntity>();

    /// <summary>Gets the swarm messages.</summary>
    public DbSet<MessageEntity> Messages => this.Set<MessageEntity>();

    /// <summary>Gets the swarm events.</summary>
    public DbSet<EventEntity> Events => this.Set<EventEntity>();

    /// <summary>Gets the swarm files.</summary>
    public DbSet<FileEntity> Files => this.Set<FileEntity>();

    /// <summary>Gets the swarm-level state transition audit rows.</summary>
    public DbSet<SwarmStateTransition> SwarmStateTransitions => this.Set<SwarmStateTransition>();

    /// <summary>Gets the task-level state transition audit rows.</summary>
    public DbSet<TaskStateTransition> TaskStateTransitions => this.Set<TaskStateTransition>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        var isPostgres = this.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        ConfigureSwarms(modelBuilder, isPostgres);
        ConfigureTasks(modelBuilder, isPostgres);
        ConfigureAgents(modelBuilder);
        ConfigureMessages(modelBuilder);
        ConfigureEvents(modelBuilder, isPostgres);
        ConfigureFiles(modelBuilder);
        ConfigureSwarmStateTransitions(modelBuilder);
        ConfigureTaskStateTransitions(modelBuilder);
    }

    private static void ConfigureSwarms(ModelBuilder modelBuilder, bool isPostgres)
    {
        modelBuilder.Entity<SwarmEntity>(entity =>
        {
            if (isPostgres)
            {
                entity.Property(e => e.Xmin)
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }
            else
            {
                entity.Ignore(e => e.Xmin);
            }
        });
    }

    private static void ConfigureTasks(ModelBuilder modelBuilder, bool isPostgres)
    {
        modelBuilder.Entity<TaskEntity>(entity =>
        {
            entity.HasKey(e => new { e.SwarmId, e.Id });

            entity.HasIndex(e => e.SwarmId)
                .HasDatabaseName("idx_tasks_swarm_id");

            if (isPostgres)
            {
                // jsonb is Npgsql-specific; on other providers the column maps to the default text
                // type so the SQLite migration set stays portable.
                entity.Property(e => e.BlockedByJson)
                    .HasColumnType("jsonb");

                entity.Property(e => e.Xmin)
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }
            else
            {
                entity.Ignore(e => e.Xmin);
            }
        });
    }

    private static void ConfigureAgents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentEntity>(entity =>
        {
            entity.HasKey(e => new { e.SwarmId, e.Name });

            entity.HasIndex(e => e.SwarmId)
                .HasDatabaseName("idx_agents_swarm_id");
        });
    }

    private static void ConfigureMessages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.HasIndex(e => new { e.SwarmId, e.CreatedAt })
                .HasDatabaseName("idx_messages_swarm_created");
        });
    }

    private static void ConfigureEvents(ModelBuilder modelBuilder, bool isPostgres)
    {
        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.HasIndex(e => new { e.SwarmId, e.CreatedAt })
                .HasDatabaseName("idx_events_swarm_created");

            entity.HasIndex(e => new { e.SwarmId, e.EventType })
                .HasDatabaseName("idx_events_swarm_type");

            if (isPostgres)
            {
                // jsonb is Npgsql-specific; other providers use the default text mapping.
                entity.Property(e => e.DataJson)
                    .HasColumnType("jsonb");
            }
        });
    }

    private static void ConfigureFiles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(entity =>
        {
            entity.HasIndex(e => new { e.SwarmId, e.Path })
                .IsUnique()
                .HasDatabaseName("idx_files_swarm_path");
        });
    }

    private static void ConfigureSwarmStateTransitions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SwarmStateTransition>(entity =>
        {
            entity.HasIndex(e => new { e.SwarmId, e.CreatedAt })
                .HasDatabaseName("idx_swarm_transitions_swarm_created");
        });
    }

    private static void ConfigureTaskStateTransitions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskStateTransition>(entity =>
        {
            entity.HasIndex(e => new { e.SwarmId, e.TaskId, e.CreatedAt })
                .HasDatabaseName("idx_task_transitions_task_created");
        });
    }
}
