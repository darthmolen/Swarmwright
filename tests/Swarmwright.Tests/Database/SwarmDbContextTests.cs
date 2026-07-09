using Swarmwright.Database;
using Swarmwright.Database.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Swarmwright.Tests.Database;

/// <summary>
/// Tests for the <see cref="SwarmDbContext"/> class.
/// </summary>
[TestClass]
public class SwarmDbContextTests
{
    private static SwarmDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SwarmDbContext>()
            .UseInMemoryDatabase("SwarmTest_" + Guid.NewGuid())
            .Options;
        return new SwarmDbContext(options);
    }

    // ---- DbSet existence tests ----

    [TestMethod]
    public void Context_Has_Swarms_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Swarms.Should().NotBeNull();
    }

    [TestMethod]
    public void Context_Has_Tasks_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Tasks.Should().NotBeNull();
    }

    [TestMethod]
    public void Context_Has_Agents_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Agents.Should().NotBeNull();
    }

    [TestMethod]
    public void Context_Has_Messages_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Messages.Should().NotBeNull();
    }

    [TestMethod]
    public void Context_Has_Events_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Events.Should().NotBeNull();
    }

    [TestMethod]
    public void Context_Has_Files_DbSet()
    {
        using var ctx = CreateContext();
        ctx.Files.Should().NotBeNull();
    }

    // ---- CRUD round-trip tests ----

    [TestMethod]
    public async Task Can_Insert_And_Read_SwarmEntity()
    {
        using var ctx = CreateContext();
        var swarm = new SwarmEntity { Goal = "Test goal" };
        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Swarms.FindAsync(swarm.Id);
        loaded.Should().NotBeNull();
        loaded!.Goal.Should().Be("Test goal");
        loaded.State.Should().Be("Created");
        loaded.CurrentRound.Should().Be(0);
        loaded.MaxRounds.Should().Be(8);
    }

    [TestMethod]
    public async Task Can_Insert_And_Read_TaskEntity_With_CompositeKey()
    {
        using var ctx = CreateContext();
        var swarmId = Guid.NewGuid();
        var swarm = new SwarmEntity { Id = swarmId, Goal = "parent" };
        ctx.Swarms.Add(swarm);

        var task = new TaskEntity
        {
            SwarmId = swarmId,
            Id = "task-1",
            Subject = "Do something",
            Description = "Details here",
        };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Tasks.FindAsync(swarmId, "task-1");
        loaded.Should().NotBeNull();
        loaded!.Subject.Should().Be("Do something");
        loaded.State.Should().Be("Pending");
    }

    [TestMethod]
    public async Task Can_Insert_And_Read_AgentEntity_With_CompositeKey()
    {
        using var ctx = CreateContext();
        var swarmId = Guid.NewGuid();
        var swarm = new SwarmEntity { Id = swarmId, Goal = "parent" };
        ctx.Swarms.Add(swarm);

        var agent = new AgentEntity
        {
            SwarmId = swarmId,
            Name = "researcher",
            Role = "Research Agent",
            DisplayName = "Researcher",
        };
        ctx.Agents.Add(agent);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Agents.FindAsync(swarmId, "researcher");
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be("idle");
        loaded.TasksCompleted.Should().Be(0);
    }

    [TestMethod]
    public async Task Can_Insert_And_Read_MessageEntity()
    {
        using var ctx = CreateContext();
        var swarmId = Guid.NewGuid();
        var swarm = new SwarmEntity { Id = swarmId, Goal = "parent" };
        ctx.Swarms.Add(swarm);

        var msg = new MessageEntity
        {
            SwarmId = swarmId,
            Sender = "orchestrator",
            Recipient = "researcher",
            Content = "Hello",
        };
        ctx.Messages.Add(msg);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Messages.FirstOrDefaultAsync(m => m.SwarmId == swarmId);
        loaded.Should().NotBeNull();
        loaded!.Content.Should().Be("Hello");
    }

    [TestMethod]
    public async Task Can_Insert_And_Read_EventEntity()
    {
        using var ctx = CreateContext();
        var evt = new EventEntity
        {
            SwarmId = Guid.NewGuid(),
            EventType = "swarm.started",
        };
        ctx.Events.Add(evt);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Events.FirstOrDefaultAsync();
        loaded.Should().NotBeNull();
        loaded!.EventType.Should().Be("swarm.started");
        loaded.DataJson.Should().Be("{}");
    }

    [TestMethod]
    public async Task EventEntity_SwarmId_Can_Be_Null()
    {
        using var ctx = CreateContext();
        var evt = new EventEntity
        {
            SwarmId = null,
            EventType = "system.startup",
        };
        ctx.Events.Add(evt);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Events.FirstOrDefaultAsync();
        loaded.Should().NotBeNull();
        loaded!.SwarmId.Should().BeNull();
    }

    [TestMethod]
    public async Task Can_Insert_And_Read_FileEntity()
    {
        using var ctx = CreateContext();
        var swarmId = Guid.NewGuid();
        var swarm = new SwarmEntity { Id = swarmId, Goal = "parent" };
        ctx.Swarms.Add(swarm);

        var file = new FileEntity
        {
            SwarmId = swarmId,
            Path = "/output/report.md",
            SizeBytes = 1024,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Files.FirstOrDefaultAsync(f => f.SwarmId == swarmId);
        loaded.Should().NotBeNull();
        loaded!.Path.Should().Be("/output/report.md");
        loaded.SizeBytes.Should().Be(1024);
    }

    // ---- Default value tests ----

    [TestMethod]
    public void SwarmEntity_Has_Correct_Defaults()
    {
        var swarm = new SwarmEntity { Goal = "test" };
        swarm.Id.Should().NotBe(Guid.Empty);
        swarm.State.Should().Be("Created");
        swarm.CurrentRound.Should().Be(0);
        swarm.MaxRounds.Should().Be(8);
        swarm.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        swarm.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void TaskEntity_Has_Correct_Defaults()
    {
        var task = new TaskEntity
        {
            SwarmId = Guid.NewGuid(),
            Id = "t1",
            Subject = "s",
            Description = "d",
        };
        task.State.Should().Be("Pending");
        task.BlockedByJson.Should().Be("[]");
        task.Result.Should().Be(string.Empty);
    }
}
