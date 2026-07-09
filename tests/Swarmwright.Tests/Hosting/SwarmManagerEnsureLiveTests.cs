using System.Collections.Concurrent;
using System.Threading.Channels;
using Swarmwright.Configuration;
using Swarmwright.Database;
using Swarmwright.Database.Models;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using Swarmwright.Tests.Hosting.StateMachine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Swarmwright.Tests.Hosting;

/// <summary>
/// Covers <see cref="SwarmManager.EnsureLiveAsync"/> — the folded-in
/// rehydration entry point that replaces the standalone
/// <c>ISwarmRehydrator</c>. Given a swarm id, the manager returns the
/// live execution (from its own dictionary), reloads it from the
/// repository + enqueues onto the dispatcher channel if it has been
/// evicted, or returns <see langword="null"/> when the swarm is
/// unknown or terminal.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmManagerEnsureLiveTests
{
    private ConcurrentDictionary<Guid, SwarmExecution> activeSwarms = null!;
    private Channel<SwarmRequest> channel = null!;
    private ISwarmRepository repository = null!;
    private InMemoryDbContextFactory factory = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.activeSwarms = new ConcurrentDictionary<Guid, SwarmExecution>();
        this.channel = Channel.CreateUnbounded<SwarmRequest>();
        this.factory = new InMemoryDbContextFactory("EnsureLive_" + Guid.NewGuid());
        this.repository = new SwarmRepository(this.factory);
    }

    [TestMethod]
    public async Task EnsureLiveAsync_WhenAlreadyLive_ReturnsExistingWithoutEnqueueing()
    {
        var swarmId = Guid.NewGuid();
        var existing = CreateExecution(swarmId);
        this.activeSwarms[swarmId] = existing;

        var manager = this.CreateManager();
        var result = await manager.EnsureLiveAsync(swarmId);

        result.Should().BeSameAs(existing);
        this.channel.Reader.Count.Should().Be(0, "an already-live swarm must not be enqueued again");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_WhenEvicted_RegistersAndEnqueues()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var manager = this.CreateManager();

        var result = await manager.EnsureLiveAsync(swarmId);

        result.Should().NotBeNull();
        result!.SwarmId.Should().Be(swarmId);
        this.activeSwarms.Should().ContainKey(swarmId, "the evicted swarm must be re-registered in the active dictionary");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var request = await this.channel.Reader.ReadAsync(cts.Token);
        request.SwarmId.Should().Be(swarmId, "EnsureLiveAsync must enqueue a SwarmRequest so the dispatcher picks it up");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_RehydratesContextFromContextJson()
    {
        var swarmId = await this.SeedSwarmAsync(
            SwarmInstanceState.AwaitingIntervention,
            contextJson: "{\"sourceRoot\":\"/clones/pr-11\"}");
        var manager = this.CreateManager();

        var result = await manager.EnsureLiveAsync(swarmId);

        result.Should().NotBeNull();
        result!.Context.Should().ContainKey("sourceRoot")
            .WhoseValue.Should().Be("/clones/pr-11");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_MalformedContextJson_DegradesToEmpty_WithoutThrowing()
    {
        var swarmId = await this.SeedSwarmAsync(
            SwarmInstanceState.AwaitingIntervention,
            contextJson: "{ not json");
        var manager = this.CreateManager();

        var act = async () => await manager.EnsureLiveAsync(swarmId);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.Should().NotBeNull();
        result!.Context.Should().BeEmpty("a malformed context row must not crash resurrection");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_BlankContextJson_DegradesToEmpty_WithoutThrowing()
    {
        var swarmId = await this.SeedSwarmAsync(
            SwarmInstanceState.AwaitingIntervention,
            contextJson: "   ");
        var manager = this.CreateManager();

        var act = async () => await manager.EnsureLiveAsync(swarmId);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.Should().NotBeNull();
        result!.Context.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EnsureLiveAsync_WhenSwarmMissingFromDb_ReturnsNull()
    {
        var manager = this.CreateManager();

        var result = await manager.EnsureLiveAsync(Guid.NewGuid());

        result.Should().BeNull();
        this.channel.Reader.Count.Should().Be(0, "no request should be enqueued for a swarm that doesn't exist");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_WhenSwarmTerminal_ReturnsNull()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.Complete);
        var manager = this.CreateManager();

        var result = await manager.EnsureLiveAsync(swarmId);

        result.Should().BeNull("a terminal swarm cannot resume");
        this.channel.Reader.Count.Should().Be(0, "no request should be enqueued for a terminal swarm");
    }

    [TestMethod]
    public async Task EnsureLiveAsync_TwoConcurrentCalls_EnqueueOnce()
    {
        var swarmId = await this.SeedSwarmAsync(SwarmInstanceState.AwaitingIntervention);
        var manager = this.CreateManager();

        var t1 = Task.Run(() => manager.EnsureLiveAsync(swarmId));
        var t2 = Task.Run(() => manager.EnsureLiveAsync(swarmId));
        var results = await Task.WhenAll(t1, t2);

        results[0].Should().NotBeNull();
        results[0].Should().BeSameAs(results[1], "concurrent callers must observe the same execution instance");

        // Drain the channel — should be exactly one request.
        var drained = 0;
        while (this.channel.Reader.TryRead(out _))
        {
            drained++;
        }

        drained.Should().Be(1, "idempotency-under-concurrency must produce a single dispatcher request");
    }

    private static SwarmExecution CreateExecution(Guid swarmId)
    {
        return new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = "existing",
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new Swarmwright.Events.AgUI.SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };
    }

    private async Task<Guid> SeedSwarmAsync(SwarmInstanceState state, string? contextJson = null)
    {
        await using var ctx = this.factory.CreateDbContext();
        var swarm = new SwarmEntity
        {
            Id = Guid.NewGuid(),
            Goal = "seeded",
            State = state.ToString(),
        };
        if (contextJson is not null)
        {
            swarm.ContextJson = contextJson;
        }

        ctx.Swarms.Add(swarm);
        await ctx.SaveChangesAsync();
        return swarm.Id;
    }

    private SwarmManager CreateManager()
    {
        return new SwarmManager(
            this.channel.Writer,
            this.activeSwarms,
            Options.Create(new SwarmOptions()),
            this.repository,
            Mock.Of<ISwarmObservationSink>(),
            NullLogger<SwarmManager>.Instance);
    }
}
