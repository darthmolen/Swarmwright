using System.Text.Json;
using System.Threading.Channels;

namespace Swarmwright.Events.AgUI;

/// <summary>
/// Central event hub that translates swarm orchestration actions into typed
/// AG-UI protocol events and writes them to a channel for SSE consumption.
/// </summary>
public sealed class SwarmEventAdapter
{
    private readonly Channel<SwarmAgUIEvent> channel;
    private string? runId;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmEventAdapter"/> class.
    /// </summary>
    public SwarmEventAdapter()
    {
        this.channel = Channel.CreateUnbounded<SwarmAgUIEvent>();
    }

    /// <summary>
    /// Gets the channel reader for consuming AG-UI events.
    /// </summary>
    public ChannelReader<SwarmAgUIEvent> Reader => this.channel.Reader;

    // -------------------------------------------------------------------
    // Lifecycle events
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits a <see cref="RunStartedEvent"/> indicating the swarm run has begun.
    /// </summary>
    /// <param name="swarmId">The swarm identifier used as threadId.</param>
    /// <param name="goal">The swarm goal description.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitRunStartedAsync(Guid swarmId, string goal)
    {
        this.runId = Guid.NewGuid().ToString("N");
        await this.channel.Writer.WriteAsync(new RunStartedEvent
        {
            ThreadId = swarmId.ToString(),
            RunId = this.runId,
            Goal = goal ?? string.Empty,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a <see cref="RunFinishedEvent"/> indicating the swarm completed successfully.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitRunFinishedAsync(Guid swarmId)
    {
        await this.channel.Writer.WriteAsync(new RunFinishedEvent
        {
            ThreadId = swarmId.ToString(),
            RunId = this.runId ?? string.Empty,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a <see cref="RunErrorEvent"/> indicating the swarm encountered an error.
    /// </summary>
    /// <param name="swarmId">The swarm identifier.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">The error description.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitRunErrorAsync(Guid swarmId, string code, string message)
    {
        _ = swarmId;
        await this.channel.Writer.WriteAsync(new RunErrorEvent
        {
            Code = code,
            Message = message,
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Step events (phase transitions)
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits a <see cref="StepStartedEvent"/> for a swarm phase beginning.
    /// </summary>
    /// <param name="phase">The phase name.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitStepStartedAsync(string phase)
    {
        await this.channel.Writer.WriteAsync(new StepStartedEvent
        {
            StepName = phase,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a <see cref="StepFinishedEvent"/> for a swarm phase completing.
    /// </summary>
    /// <param name="phase">The phase name.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitStepFinishedAsync(string phase)
    {
        await this.channel.Writer.WriteAsync(new StepFinishedEvent
        {
            StepName = phase,
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // State events
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits a <see cref="StateSnapshotEvent"/> with a complete state representation.
    /// </summary>
    /// <param name="snapshot">The state snapshot as a JSON element.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitStateSnapshotAsync(JsonElement snapshot)
    {
        await this.channel.Writer.WriteAsync(new StateSnapshotEvent
        {
            Snapshot = snapshot,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a <see cref="StateDeltaEvent"/> with RFC 6902 JSON Patch operations.
    /// </summary>
    /// <param name="patch">The JSON Patch operations array.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitStateDeltaAsync(JsonElement patch)
    {
        await this.channel.Writer.WriteAsync(new StateDeltaEvent
        {
            Delta = patch,
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Custom domain events
    // -------------------------------------------------------------------

    /// <summary>
    /// Emits a <see cref="SwarmCustomEvent"/> for swarm-specific domain events.
    /// </summary>
    /// <param name="name">The custom event name (e.g. SWARM_TASK_UPDATED).</param>
    /// <param name="value">The event payload as a JSON element.</param>
    /// <param name="agentName">The optional agent name associated with this event.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitCustomAsync(string name, JsonElement value, string? agentName = null)
    {
        await this.channel.Writer.WriteAsync(new SwarmCustomEvent
        {
            Name = name,
            Value = value,
            AgentName = agentName,
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Pass-through (for interceptor-generated events)
    // -------------------------------------------------------------------

    /// <summary>
    /// Writes any <see cref="SwarmAgUIEvent"/> directly to the channel.
    /// Used by the <c>AgUIEventInterceptor</c> to forward events from the chat client pipeline.
    /// </summary>
    /// <param name="evt">The event to emit.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EmitAsync(SwarmAgUIEvent evt)
    {
        await this.channel.Writer.WriteAsync(evt).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that no more events will be written.
    /// </summary>
    public void Complete()
    {
        this.channel.Writer.TryComplete();
    }
}
