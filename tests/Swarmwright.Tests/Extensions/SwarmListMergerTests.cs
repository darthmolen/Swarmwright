using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Events.AgUI;
using Swarmwright.Extensions;
using Swarmwright.Hosting;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Tests for <see cref="SwarmListMerger.Merge"/>, which unions the in-memory
/// active swarm list with the database-backed historical list into a single
/// sorted projection.
/// </summary>
[TestClass]
public sealed class SwarmListMergerTests
{
    /// <summary>
    /// Verifies that merging empty inputs returns an empty collection.
    /// </summary>
    [TestMethod]
    public void Merge_EmptySources_ReturnsEmpty()
    {
        var result = SwarmListMerger.Merge(
            Array.Empty<SwarmExecution>(),
            Array.Empty<SwarmListEntry>(),
            limit: 50);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that when an active execution has no matching historical entry,
    /// the merge projects the execution with a "Running" phase fallback.
    /// (Post state-machine migration, phase is sourced from the DB; this is
    /// the transient window between manager insertion and the first DB write
    /// and should rarely occur in practice since SwarmService.CreateSwarmAsync
    /// persists immediately.)
    /// </summary>
    [TestMethod]
    public void Merge_ActiveWithoutHistorical_UsesRunningFallback()
    {
        var active = new[]
        {
            CreateExecution(Guid.NewGuid(), "goal-a", "deep-research", SwarmInstanceState.Executing),
        };

        var result = SwarmListMerger.Merge(active, Array.Empty<SwarmListEntry>(), limit: 50);

        result.Should().ContainSingle();
        result[0].SwarmId.Should().Be(active[0].SwarmId);
        result[0].Goal.Should().Be("goal-a");
        result[0].TemplateKey.Should().Be("deep-research");
        result[0].Phase.Should().Be("Running");
        result[0].TaskCount.Should().Be(0);
        result[0].WorkerCount.Should().Be(0);
        result[0].CompletedAt.Should().BeNull();
    }

    /// <summary>
    /// Verifies that a merge of historical-only inputs projects each <see cref="SwarmListEntry"/>.
    /// </summary>
    [TestMethod]
    public void Merge_HistoricalOnly_ReturnsHistoricalProjected()
    {
        var now = DateTime.UtcNow;
        var historical = new[]
        {
            new SwarmListEntry(
                Guid.NewGuid(),
                "old-goal",
                "deep-research",
                "Complete",
                IsRunning: false,
                CreatedAt: now.AddMinutes(-30),
                CompletedAt: now.AddMinutes(-20),
                LastEventAt: now.AddMinutes(-20),
                TaskCount: 7,
                WorkerCount: 3),
        };

        var result = SwarmListMerger.Merge(Array.Empty<SwarmExecution>(), historical, limit: 50);

        result.Should().ContainSingle();
        result[0].Goal.Should().Be("old-goal");
        result[0].Phase.Should().Be("Complete");
        result[0].IsRunning.Should().BeFalse();
        result[0].TaskCount.Should().Be(7);
        result[0].WorkerCount.Should().Be(3);
        result[0].CompletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that when the same swarm id appears in both sources, the DB
    /// phase wins (post state-machine migration the DB is the single source
    /// of truth for state), counts come from the DB, and IsRunning comes from
    /// the in-memory execution.
    /// </summary>
    [TestMethod]
    public void Merge_SameIdInBoth_DbPhaseWins()
    {
        var swarmId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // DB snapshot: Synthesizing phase (authoritative), counts 12/4.
        var historical = new[]
        {
            new SwarmListEntry(
                swarmId,
                "same-goal",
                "deep-research",
                "Synthesizing",
                IsRunning: false,
                CreatedAt: now.AddMinutes(-10),
                CompletedAt: null,
                LastEventAt: now.AddMinutes(-8),
                TaskCount: 12,
                WorkerCount: 4),
        };

        var active = new[]
        {
            CreateExecution(swarmId, "same-goal", "deep-research", SwarmInstanceState.Synthesizing),
        };

        var result = SwarmListMerger.Merge(active, historical, limit: 50);

        result.Should().ContainSingle();
        var entry = result[0];

        // DB is the source of truth for phase after the state-machine migration.
        entry.Phase.Should().Be("Synthesizing");

        // DB wins on fields the in-memory projection does not carry.
        entry.TaskCount.Should().Be(12);
        entry.WorkerCount.Should().Be(4);
    }

    /// <summary>
    /// Verifies that merged entries are sorted by their effective activity timestamp descending.
    /// </summary>
    [TestMethod]
    public void Merge_OrdersByLastEventDescending()
    {
        var now = DateTime.UtcNow;
        var oldest = new SwarmListEntry(
            Guid.NewGuid(),
            "oldest",
            null,
            "Complete",
            IsRunning: false,
            CreatedAt: now.AddMinutes(-30),
            CompletedAt: now.AddMinutes(-25),
            LastEventAt: now.AddMinutes(-25),
            TaskCount: 0,
            WorkerCount: 0);
        var middle = new SwarmListEntry(
            Guid.NewGuid(),
            "middle",
            null,
            "Complete",
            IsRunning: false,
            CreatedAt: now.AddMinutes(-20),
            CompletedAt: now.AddMinutes(-15),
            LastEventAt: now.AddMinutes(-15),
            TaskCount: 0,
            WorkerCount: 0);
        var newest = new SwarmListEntry(
            Guid.NewGuid(),
            "newest",
            null,
            "Complete",
            IsRunning: false,
            CreatedAt: now.AddMinutes(-10),
            CompletedAt: now.AddMinutes(-5),
            LastEventAt: now.AddMinutes(-5),
            TaskCount: 0,
            WorkerCount: 0);

        var result = SwarmListMerger.Merge(
            Array.Empty<SwarmExecution>(),
            new[] { oldest, middle, newest },
            limit: 50);

        result.Select(r => r.Goal).Should().Equal("newest", "middle", "oldest");
    }

    /// <summary>
    /// Verifies that the limit applies to the historical slice only, so all active
    /// entries are always included regardless of the limit value.
    /// </summary>
    [TestMethod]
    public void Merge_AppliesLimitToHistoricalOnly_AllActiveIncluded()
    {
        var active = Enumerable.Range(0, 60)
            .Select(i => CreateExecution(Guid.NewGuid(), $"active-{i}", null, SwarmInstanceState.Executing))
            .ToArray();
        var now = DateTime.UtcNow;
        var historical = Enumerable.Range(0, 20)
            .Select(i => new SwarmListEntry(
                Guid.NewGuid(),
                $"hist-{i}",
                null,
                "Complete",
                IsRunning: false,
                CreatedAt: now.AddMinutes(-60 - i),
                CompletedAt: now.AddMinutes(-50 - i),
                LastEventAt: now.AddMinutes(-50 - i),
                TaskCount: 0,
                WorkerCount: 0))
            .ToArray();

        var result = SwarmListMerger.Merge(active, historical, limit: 50);

        result.Should().HaveCount(60);
        result.Where(r => r.Goal.StartsWith("active-", StringComparison.Ordinal)).Should().HaveCount(60);
        result.Where(r => r.Goal.StartsWith("hist-", StringComparison.Ordinal)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that when there are more active entries than the limit, none are truncated.
    /// </summary>
    [TestMethod]
    public void Merge_WhenActiveExceedsLimit_DoesNotTruncateActive()
    {
        var active = Enumerable.Range(0, 100)
            .Select(i => CreateExecution(Guid.NewGuid(), $"active-{i}", null, SwarmInstanceState.Executing))
            .ToArray();

        var result = SwarmListMerger.Merge(active, Array.Empty<SwarmListEntry>(), limit: 10);

        result.Should().HaveCount(100);
    }

    private static SwarmExecution CreateExecution(Guid swarmId, string goal, string? templateKey, SwarmInstanceState phase)
    {
        _ = phase;
        return new SwarmExecution
        {
            SwarmId = swarmId,
            Goal = goal,
            TemplateKey = templateKey,
            Cts = new CancellationTokenSource(),
            EventBus = new SwarmEventBus(),
            AgUiAdapter = new SwarmEventAdapter(),
            WorkDirectory = Path.GetTempPath(),
        };
    }
}
