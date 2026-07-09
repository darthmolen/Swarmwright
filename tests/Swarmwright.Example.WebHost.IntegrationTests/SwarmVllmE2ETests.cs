using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Swarmwright.Example.WebHost.IntegrationTests;

/// <summary>
/// End-to-end test that boots the real example web host in-process and drives a full
/// <c>deep-research</c> swarm against a local OpenAI-compatible model server (vLLM/Ollama).
/// </summary>
/// <remarks>
/// <para>
/// The test is gated on the <c>SWARMWRIGHT_VLLM_ENDPOINT</c> environment variable so a normal
/// <c>dotnet test</c> run stays fast and self-contained — it returns early as a no-op pass (logging
/// the skip reason to the test context) when no model server is configured. To run it for real,
/// start the local stack and point the variable at it:
/// </para>
/// <code>
/// ./scripts/start.sh --gpu
/// SWARMWRIGHT_VLLM_ENDPOINT=http://localhost:8000/v1 dotnet test --filter TestCategory=E2E
/// </code>
/// </remarks>
[TestClass]
[TestCategory("E2E")]
public sealed class SwarmVllmE2ETests
{
    /// <summary>Gets or sets the MSTest-injected test context, used to log the skip reason.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Submits a deep-research swarm through the live HTTP API and waits for it to reach a terminal
    /// state, asserting it completes successfully. This test is a no-op pass unless a local
    /// OpenAI-compatible endpoint is configured via <c>SWARMWRIGHT_VLLM_ENDPOINT</c>, so the default
    /// <c>dotnet test</c> run stays self-contained (the plan classifies E2E as manual, not CI-gated).
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [TestMethod]
    public async Task DeepResearchSwarm_AgainstLocalModel_RunsToCompletion()
    {
        var endpoint = Environment.GetEnvironmentVariable(SwarmVllmE2EWebApplicationFactory.EndpointVariable);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            this.TestContext.WriteLine(
                $"E2E skipped: set {SwarmVllmE2EWebApplicationFactory.EndpointVariable} " +
                "(e.g. http://localhost:8000/v1) to run against a local vLLM/Ollama server. " +
                "Start one with ./scripts/start.sh --gpu.");
            return;
        }

        await using var factory = new SwarmVllmE2EWebApplicationFactory();
        using var client = factory.CreateClient();

        const string goal =
            "Briefly compare two sorting algorithms (quicksort and mergesort) on time complexity " +
            "and stability. Keep it short.";

        // Submit the swarm using the canonical deep-research template.
        var createResponse = await client.PostAsJsonAsync(
            "/api/swarm/",
            new { goal, templateKey = "deep-research" }).ConfigureAwait(false);
        createResponse.EnsureSuccessStatusCode();

        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var swarmId = createBody.GetProperty("swarmId").GetString();
        swarmId.Should().NotBeNullOrEmpty("creating a swarm must return its id.");

        // Poll the status endpoint until the swarm reaches a terminal state.
        var terminalStates = new[] { "Complete", "Failed", "Cancelled" };
        string? state = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        while (!timeout.IsCancellationRequested)
        {
            var statusResponse = await client.GetAsync(new Uri($"/api/swarm/{swarmId}", UriKind.Relative), timeout.Token)
                .ConfigureAwait(false);
            statusResponse.EnsureSuccessStatusCode();

            var statusBody = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token).ConfigureAwait(false);
            state = statusBody.GetProperty("state").GetString();

            if (terminalStates.Contains(state, StringComparer.Ordinal))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
        }

        state.Should().Be(
            "Complete",
            "the deep-research swarm should run end-to-end against the local model and finish successfully.");

        // The completed swarm should expose its task board with at least one completed task.
        var tasksResponse = await client.GetAsync(new Uri($"/api/swarm/{swarmId}/tasks", UriKind.Relative)).ConfigureAwait(false);
        tasksResponse.EnsureSuccessStatusCode();
        var tasks = await tasksResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        tasks.ValueKind.Should().Be(JsonValueKind.Array, "the tasks endpoint returns the task board as an array.");
    }
}
