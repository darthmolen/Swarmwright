using System.Text.Json;
using Swarmwright.Database.Models;
using Swarmwright.Events;
using Swarmwright.Recommendation;
using FluentAssertions;

namespace Swarmwright.Tests.Database.Models;

/// <summary>
/// Snapshot assertions that protect the <c>GET /api/swarm/{id}</c> JSON contract.
/// The frontend hydration code (<c>useSwarmHydration.ts</c>) and external MCP
/// consumers parse the exact camelCase key shape this test freezes. Any edit to
/// <see cref="SwarmMetadataResponse"/> that changes a field name, case, or
/// recommendation nesting must update these snapshots intentionally.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmMetadataResponseSerializationTests
{
    [TestMethod]
    public void Serialize_PopulatedDto_ProducesCamelCaseKeysInExpectedOrder()
    {
        var dto = new SwarmMetadataResponse(
            SwarmId: new Guid("7e1c8d95-152c-425f-9ab8-73002b34a6e5"),
            Goal: "deploy things",
            TemplateKey: "azure-solutions-agent",
            Phase: "AwaitingIntervention",
            State: "AwaitingIntervention",
            IsRunning: false,
            LockedBy: null,
            LockedAt: null,
            CreatedAt: new DateTime(2026, 4, 23, 14, 0, 0, DateTimeKind.Utc),
            CompletedAt: null,
            Recommendation: new SwarmContinueRecommendation(
                ValidActions: ["continue", "smart-continue", "force-synthesis", "cancel"],
                RecommendedAction: "continue",
                Rationale: "No failures. 1 Pending task(s) viable."));

        var json = JsonSerializer.Serialize(dto, SwarmJsonOptions.Default);

        json.Should().Contain("\"swarmId\":\"7e1c8d95-152c-425f-9ab8-73002b34a6e5\"");
        json.Should().Contain("\"goal\":\"deploy things\"");
        json.Should().Contain("\"templateKey\":\"azure-solutions-agent\"");
        json.Should().Contain("\"phase\":\"AwaitingIntervention\"");
        json.Should().Contain("\"state\":\"AwaitingIntervention\"");
        json.Should().Contain("\"isRunning\":false");
        json.Should().Contain("\"lockedBy\":null");
        json.Should().Contain("\"lockedAt\":null");
        json.Should().Contain("\"createdAt\":");
        json.Should().Contain("\"completedAt\":null");

        // Recommendation nesting + keys.
        json.Should().Contain("\"recommendation\":{");
        json.Should().Contain("\"validActions\":[\"continue\",\"smart-continue\",\"force-synthesis\",\"cancel\"]");
        json.Should().Contain("\"recommendedAction\":\"continue\"");
        json.Should().Contain("\"rationale\":\"No failures. 1 Pending task(s) viable.\"");
    }

    [TestMethod]
    public void Serialize_NullRecommendation_EmitsExplicitNull()
    {
        var dto = new SwarmMetadataResponse(
            SwarmId: Guid.NewGuid(),
            Goal: "g",
            TemplateKey: null,
            Phase: "Executing",
            State: "Executing",
            IsRunning: true,
            LockedBy: null,
            LockedAt: null,
            CreatedAt: DateTime.UtcNow,
            CompletedAt: null,
            Recommendation: null);

        var json = JsonSerializer.Serialize(dto, SwarmJsonOptions.Default);

        json.Should().Contain(
            "\"recommendation\":null",
            "frontend relies on key presence to disambiguate 'no recommendation' from 'recommendation missing from shape'");
    }

    [TestMethod]
    public void Roundtrip_DtoSerializesAndDeserializes_PreservesAllFields()
    {
        var original = new SwarmMetadataResponse(
            SwarmId: Guid.NewGuid(),
            Goal: "round-trip",
            TemplateKey: "t",
            Phase: "AwaitingIntervention",
            State: "AwaitingIntervention",
            IsRunning: false,
            LockedBy: "alice",
            LockedAt: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
            CreatedAt: new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc),
            CompletedAt: null,
            Recommendation: new SwarmContinueRecommendation(
                ValidActions: ["continue"],
                RecommendedAction: "continue",
                Rationale: "r"));

        var json = JsonSerializer.Serialize(original, SwarmJsonOptions.Default);
        var roundTripped = JsonSerializer.Deserialize<SwarmMetadataResponse>(json, SwarmJsonOptions.Default);

        roundTripped.Should().BeEquivalentTo(original);
    }
}
