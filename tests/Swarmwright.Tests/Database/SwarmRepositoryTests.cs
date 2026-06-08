using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Tests.Database;

/// <summary>
/// Tests for the <see cref="SwarmRepository"/> class.
/// </summary>
[TestClass]
public class SwarmRepositoryTests
{
    private static (SwarmDbContext Context, SwarmRepository Repo) CreateSut()
    {
        var options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseInMemoryDatabase("SwarmTest_" + Guid.NewGuid())
            .Options;
        var ctx = new SwarmDbContext(options);
        var factory = new InMemoryDbContextFactory(options);
        var repo = new SwarmRepository(factory);
        return (ctx, repo);
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

    // ---- SwarmEntity defaults ----

    /// <summary>
    /// Verifies that the default <see cref="SwarmEntity.State"/> value is PascalCase
    /// (<c>"Created"</c>), matching the <see cref="Swarmwright.Models.Enums.SwarmInstanceState.Created"/> enum member name.
    /// </summary>
    [TestMethod]
    public void SwarmEntity_DefaultState_IsPascalCase()
    {
        var entity = new SwarmEntity { Id = Guid.NewGuid(), Goal = "test" };
        entity.State.Should().Be("Created");
    }

    // ---- Swarm CRUD ----

    [TestMethod]
    public async Task CreateSwarmAsync_Persists_Swarm()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity { Goal = "Research AI safety" };
            await repo.CreateSwarmAsync(swarm);

            var loaded = await ctx.Swarms.FindAsync(swarm.Id);
            loaded.Should().NotBeNull();
            loaded!.Goal.Should().Be("Research AI safety");
        }
    }

    /// <summary>
    /// Verifies that the <see cref="SwarmEntity.ContextJson"/> column defaults to
    /// an empty JSON object so existing/new rows carry a valid serialized context.
    /// </summary>
    [TestMethod]
    public void SwarmEntity_DefaultContextJson_IsEmptyObject()
    {
        var entity = new SwarmEntity { Id = Guid.NewGuid(), Goal = "test" };
        entity.ContextJson.Should().Be("{}");
    }

    /// <summary>
    /// Verifies that a serialized context round-trips through the plain-string
    /// <c>ContextJson</c> column (must work under the InMemory provider).
    /// </summary>
    [TestMethod]
    public async Task CreateSwarmAsync_PersistsContextJson()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity
            {
                Goal = "Research AI safety",
                ContextJson = "{\"sourceRoot\":\"/clones/pr-3\"}",
            };
            await repo.CreateSwarmAsync(swarm);

            ctx.ChangeTracker.Clear();
            var loaded = await ctx.Swarms.FindAsync(swarm.Id);
            loaded.Should().NotBeNull();
            loaded!.ContextJson.Should().Be("{\"sourceRoot\":\"/clones/pr-3\"}");
        }
    }

    [TestMethod]
    public async Task GetSwarmAsync_Returns_Swarm_When_Exists()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity { Goal = "Test" };
            ctx.Swarms.Add(swarm);
            await ctx.SaveChangesAsync();

            var loaded = await repo.GetSwarmAsync(swarm.Id);
            loaded.Should().NotBeNull();
            loaded!.Goal.Should().Be("Test");
        }
    }

    [TestMethod]
    public async Task GetSwarmAsync_Returns_Null_When_Not_Found()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var loaded = await repo.GetSwarmAsync(Guid.NewGuid());
            loaded.Should().BeNull();
        }
    }

    [TestMethod]
    public async Task UpdateRoundAsync_Updates_Round_And_Timestamp()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarm = new SwarmEntity { Goal = "Test" };
            ctx.Swarms.Add(swarm);
            await ctx.SaveChangesAsync();

            await repo.UpdateRoundAsync(swarm.Id, 3);

            ctx.ChangeTracker.Clear();
            var loaded = await ctx.Swarms.FindAsync(swarm.Id);
            loaded!.CurrentRound.Should().Be(3);
            loaded.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ListSwarmsByStateAsync_Filters_By_State()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            ctx.Swarms.Add(new SwarmEntity { Goal = "A", State = "Planning" });
            ctx.Swarms.Add(new SwarmEntity { Goal = "B", State = "Executing" });
            ctx.Swarms.Add(new SwarmEntity { Goal = "C", State = "Complete" });
            ctx.Swarms.Add(new SwarmEntity { Goal = "D", State = "Planning" });
            await ctx.SaveChangesAsync();

            var result = await repo.ListSwarmsByStateAsync("Planning", "Executing");
            result.Should().HaveCount(3);
            result.Select(s => s.Goal).Should().BeEquivalentTo("A", "B", "D");
        }
    }

    // ---- Task CRUD ----

    [TestMethod]
    public async Task CreateTaskAsync_Persists_Task()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            await ctx.SaveChangesAsync();

            var task = new TaskEntity
            {
                SwarmId = swarmId,
                Id = "task-1",
                Subject = "Research",
                Description = "Research topic",
            };
            await repo.CreateTaskAsync(task);

            var loaded = await ctx.Tasks.FindAsync(swarmId, "task-1");
            loaded.Should().NotBeNull();
            loaded!.Subject.Should().Be("Research");
        }
    }

    [TestMethod]
    public async Task GetTasksAsync_Returns_Tasks_For_Swarm()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarmId, Id = "t1", Subject = "A", Description = "d" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarmId, Id = "t2", Subject = "B", Description = "d" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = Guid.NewGuid(), Id = "t3", Subject = "C", Description = "d" });
            await ctx.SaveChangesAsync();

            var tasks = await repo.GetTasksAsync(swarmId);
            tasks.Should().HaveCount(2);
        }
    }

    // Task status/result mutations are now written by StateTransitionService
    // (see StateTransitionServiceTests). The legacy repository-level
    // UpdateTaskStatusAsync method was dropped in Phase B5 cleanup.

    // ---- Agent CRUD ----

    [TestMethod]
    public async Task RegisterAgentAsync_Persists_Agent()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            await ctx.SaveChangesAsync();

            var agent = new AgentEntity
            {
                SwarmId = swarmId,
                Name = "researcher",
                Role = "Research",
                DisplayName = "Researcher",
            };
            await repo.RegisterAgentAsync(agent);

            var loaded = await ctx.Agents.FindAsync(swarmId, "researcher");
            loaded.Should().NotBeNull();
        }
    }

    [TestMethod]
    public async Task GetAgentsAsync_Returns_Agents_For_Swarm()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Agents.Add(new AgentEntity { SwarmId = swarmId, Name = "a1", Role = "R", DisplayName = "A1" });
            ctx.Agents.Add(new AgentEntity { SwarmId = swarmId, Name = "a2", Role = "R", DisplayName = "A2" });
            await ctx.SaveChangesAsync();

            var agents = await repo.GetAgentsAsync(swarmId);
            agents.Should().HaveCount(2);
        }
    }

    [TestMethod]
    public async Task UpdateAgentStatusAsync_Updates_Status()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Agents.Add(new AgentEntity { SwarmId = swarmId, Name = "a1", Role = "R", DisplayName = "A1" });
            await ctx.SaveChangesAsync();

            await repo.UpdateAgentStatusAsync(swarmId, "a1", "working");

            ctx.ChangeTracker.Clear();
            var loaded = await ctx.Agents.FindAsync(swarmId, "a1");
            loaded!.Status.Should().Be("working");
        }
    }

    // ---- Message CRUD ----

    [TestMethod]
    public async Task SaveMessageAsync_Persists_Message()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            await ctx.SaveChangesAsync();

            var msg = new MessageEntity
            {
                SwarmId = swarmId,
                Sender = "orch",
                Recipient = "worker",
                Content = "Do this",
            };
            await repo.SaveMessageAsync(msg);

            var loaded = await ctx.Messages.FirstOrDefaultAsync(m => m.SwarmId == swarmId);
            loaded.Should().NotBeNull();
            loaded!.Content.Should().Be("Do this");
        }
    }

    [TestMethod]
    public async Task GetMessagesAsync_Returns_Messages_Ordered_By_CreatedAt()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Messages.Add(new MessageEntity { SwarmId = swarmId, Sender = "a", Recipient = "b", Content = "first", CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
            ctx.Messages.Add(new MessageEntity { SwarmId = swarmId, Sender = "a", Recipient = "b", Content = "second", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            ctx.Messages.Add(new MessageEntity { SwarmId = swarmId, Sender = "a", Recipient = "b", Content = "third", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();

            var messages = await repo.GetMessagesAsync(swarmId);
            messages.Should().HaveCount(3);
            messages[0].Content.Should().Be("first");
            messages[2].Content.Should().Be("third");
        }
    }

    // ---- Event CRUD ----

    [TestMethod]
    public async Task SaveEventAsync_Persists_Event()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var evt = new EventEntity
            {
                SwarmId = Guid.NewGuid(),
                EventType = "swarm.created",
                DataJson = "{\"key\":\"value\"}",
            };
            await repo.SaveEventAsync(evt);

            var loaded = await ctx.Events.FirstOrDefaultAsync();
            loaded.Should().NotBeNull();
            loaded!.EventType.Should().Be("swarm.created");
        }
    }

    [TestMethod]
    public async Task GetEventsAsync_Returns_Events_For_Swarm()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "a", CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "b", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            ctx.Events.Add(new EventEntity { SwarmId = Guid.NewGuid(), EventType = "c" });
            await ctx.SaveChangesAsync();

            var events = await repo.GetEventsAsync(swarmId, null);
            events.Should().HaveCount(2);
        }
    }

    [TestMethod]
    public async Task GetEventsAsync_Respects_Limit()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "a", CreatedAt = DateTime.UtcNow.AddMinutes(-3) });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "b", CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
            ctx.Events.Add(new EventEntity { SwarmId = swarmId, EventType = "c", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            await ctx.SaveChangesAsync();

            var events = await repo.GetEventsAsync(swarmId, 2);
            events.Should().HaveCount(2);
        }
    }

    // ---- File CRUD ----

    [TestMethod]
    public async Task SaveFileAsync_Persists_File()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            await ctx.SaveChangesAsync();

            var file = new FileEntity
            {
                SwarmId = swarmId,
                Path = "/output/report.md",
                SizeBytes = 2048,
            };
            await repo.SaveFileAsync(file);

            var loaded = await ctx.Files.FirstOrDefaultAsync(f => f.SwarmId == swarmId);
            loaded.Should().NotBeNull();
            loaded!.SizeBytes.Should().Be(2048);
        }
    }

    [TestMethod]
    public async Task GetFilesAsync_Returns_Files_For_Swarm()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Files.Add(new FileEntity { SwarmId = swarmId, Path = "/a.txt", SizeBytes = 10 });
            ctx.Files.Add(new FileEntity { SwarmId = swarmId, Path = "/b.txt", SizeBytes = 20 });
            await ctx.SaveChangesAsync();

            var files = await repo.GetFilesAsync(swarmId);
            files.Should().HaveCount(2);
        }
    }

    // ---- LoadSwarmStateAsync ----

    [TestMethod]
    public async Task LoadSwarmStateAsync_Returns_Full_State()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var swarmId = Guid.NewGuid();
            ctx.Swarms.Add(new SwarmEntity { Id = swarmId, Goal = "parent" });
            ctx.Tasks.Add(new TaskEntity { SwarmId = swarmId, Id = "t1", Subject = "S", Description = "D" });
            ctx.Agents.Add(new AgentEntity { SwarmId = swarmId, Name = "a1", Role = "R", DisplayName = "A1" });
            ctx.Messages.Add(new MessageEntity { SwarmId = swarmId, Sender = "x", Recipient = "y", Content = "hi" });
            await ctx.SaveChangesAsync();

            var (swarm, tasks, agents, messages) = await repo.LoadSwarmStateAsync(swarmId);
            swarm.Should().NotBeNull();
            swarm!.Goal.Should().Be("parent");
            tasks.Should().HaveCount(1);
            agents.Should().HaveCount(1);
            messages.Should().HaveCount(1);
        }
    }

    [TestMethod]
    public async Task LoadSwarmStateAsync_Returns_Null_Swarm_When_Not_Found()
    {
        var (ctx, repo) = CreateSut();
        using (ctx)
        {
            var (swarm, tasks, agents, messages) = await repo.LoadSwarmStateAsync(Guid.NewGuid());
            swarm.Should().BeNull();
            tasks.Should().BeEmpty();
            agents.Should().BeEmpty();
            messages.Should().BeEmpty();
        }
    }
}
