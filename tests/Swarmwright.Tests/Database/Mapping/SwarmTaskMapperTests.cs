using Swarmwright.Database.Mapping;
using Swarmwright.Database.Models;
using Swarmwright.Models.Enums;
using FluentAssertions;

namespace Swarmwright.Tests.Database.Mapping;

/// <summary>
/// Tests for <see cref="SwarmTaskMapper"/> — the single translation point
/// between the DB <see cref="TaskEntity"/> shape (string state, JSON-encoded
/// blocked-by list) and the <see cref="Swarmwright.Models.SwarmTask"/> domain shape
/// (enum status, string list). The <c>/tasks</c> REST endpoint and the
/// rehydration path both go through this mapper so the live and
/// rehydrated task wire shapes stay aligned.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmTaskMapperTests
{
    /// <summary>
    /// Verifies scalar fields round-trip from entity to domain object.
    /// </summary>
    [TestMethod]
    public void FromEntity_CopiesScalarFields()
    {
        var swarmId = Guid.NewGuid();
        var entity = new TaskEntity
        {
            SwarmId = swarmId,
            Id = "t-42",
            Subject = "Ship the thing",
            Description = "Do the work",
            WorkerRole = "engineer",
            WorkerName = "engineer-alpha",
            State = "Pending",
            BlockedByJson = "[]",
            Result = "done",
        };

        var task = SwarmTaskMapper.FromEntity(entity);

        task.SwarmId.Should().Be(swarmId);
        task.Id.Should().Be("t-42");
        task.Subject.Should().Be("Ship the thing");
        task.Description.Should().Be("Do the work");
        task.WorkerRole.Should().Be("engineer");
        task.WorkerName.Should().Be("engineer-alpha");
        task.Result.Should().Be("done");
    }

    /// <summary>
    /// Verifies the stringly-typed <c>State</c> column parses into the
    /// <see cref="TaskState"/> enum on the domain object.
    /// </summary>
    [TestMethod]
    public void FromEntity_ParsesStateToEnum()
    {
        var entity = new TaskEntity
        {
            Id = "t",
            Subject = "s",
            Description = "d",
            State = "Completed",
            BlockedByJson = "[]",
        };

        var task = SwarmTaskMapper.FromEntity(entity);

        task.Status.Should().Be(TaskState.Completed);
    }

    /// <summary>
    /// Verifies <c>BlockedByJson</c> parses into a populated <c>BlockedBy</c> list.
    /// </summary>
    [TestMethod]
    public void FromEntity_ParsesBlockedByJsonIntoList()
    {
        var entity = new TaskEntity
        {
            Id = "t",
            Subject = "s",
            Description = "d",
            State = "Blocked",
            BlockedByJson = "[\"t1\",\"t2\"]",
        };

        var task = SwarmTaskMapper.FromEntity(entity);

        task.BlockedBy.Should().Equal("t1", "t2");
    }

    /// <summary>
    /// Verifies an empty <c>BlockedByJson</c> array produces an empty list (not null).
    /// </summary>
    [TestMethod]
    public void FromEntity_WhenBlockedByJsonEmpty_ReturnsEmptyList()
    {
        var entity = new TaskEntity
        {
            Id = "t",
            Subject = "s",
            Description = "d",
            State = "Pending",
            BlockedByJson = "[]",
        };

        var task = SwarmTaskMapper.FromEntity(entity);

        task.BlockedBy.Should().BeEmpty();
    }
}
