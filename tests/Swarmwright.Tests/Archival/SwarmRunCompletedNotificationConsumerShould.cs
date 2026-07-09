using System.Reflection;
using Swarmwright.Archival;
using Swarmwright.Events;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Swarmwright.Tests.Archival;

/// <summary>
/// Tests for <see cref="SwarmRunCompletedNotificationConsumer"/> — the
/// background handler that delegates run archival to <see cref="ISwarmRunArchiver"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmRunCompletedNotificationConsumerShould
{
    /// <summary>
    /// Verifies that handling a notification delegates to the archiver exactly once.
    /// </summary>
    [TestMethod]
    public async Task DelegateToArchiverExactlyOnce()
    {
        var archiver = new CountingArchiver();
        var consumer = new SwarmRunCompletedNotificationConsumer(archiver, NullLoggerFactory.Instance);

        await consumer.HandleAsync(NewNotification(), CancellationToken.None);

        archiver.CallCount.Should().Be(1, "the consumer must delegate the archive to ISwarmRunArchiver exactly once.");
    }

    /// <summary>
    /// Verifies that a throwing archiver does not propagate out of the handler —
    /// archival is best-effort and must never escape the consumer.
    /// </summary>
    [TestMethod]
    public async Task SwallowArchiverFailures()
    {
        var archiver = new ThrowingArchiver();
        var consumer = new SwarmRunCompletedNotificationConsumer(archiver, NullLoggerFactory.Instance);

        Func<Task> act = () => consumer.HandleAsync(NewNotification(), CancellationToken.None);

        await act.Should().NotThrowAsync("a failed archive must be logged and swallowed, never propagated.");
    }

    /// <summary>
    /// Verifies the handler implements the swarm notification handler contract so the
    /// background pipeline discovers and invokes it for completed-run notifications.
    /// </summary>
    [TestMethod]
    public void ImplementSwarmNotificationHandlerContract()
    {
        typeof(ISwarmNotificationHandler<SwarmRunCompletedNotification>)
            .IsAssignableFrom(typeof(SwarmRunCompletedNotificationConsumer))
            .Should().BeTrue("the consumer must be discoverable as a notification handler.");
    }

    private static SwarmRunCompletedNotification NewNotification() => new()
    {
        SwarmId = Guid.NewGuid(),
        WorkDirectory = Path.GetTempPath(),
        Goal = "goal",
        TemplateKey = "templateKey",
        CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
        CompletedUtc = DateTime.UtcNow,
        FinalState = SwarmInstanceState.Complete,
        FailureReason = null,
    };

    private sealed class CountingArchiver : ISwarmRunArchiver
    {
        public int CallCount { get; private set; }

        public Task ArchiveAsync(SwarmRunArchiveContext context, CancellationToken cancellationToken)
        {
            this.CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingArchiver : ISwarmRunArchiver
    {
        public Task ArchiveAsync(SwarmRunArchiveContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated archive failure.");
        }
    }
}
