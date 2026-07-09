using Swarmwright.Events.AgUI;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Events;

/// <summary>
/// Verifies that <see cref="AgUIEventInterceptor"/> does NOT dispose the inner
/// <see cref="IChatClient"/> when its own <c>Dispose</c> is called. The inner
/// client is a DI-managed singleton and the interceptor is a per-worker wrapper —
/// disposing the inner on one request's <c>using</c> block would break every
/// subsequent swarm run and refinement call across the whole host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AgUIEventInterceptorDisposeTests
{
    /// <summary>
    /// Disposing the interceptor must not propagate Dispose to the shared inner client.
    /// </summary>
    [TestMethod]
    public void Dispose_DoesNotDisposeInnerChatClient()
    {
        using var inner = new DisposeTrackingChatClient();
        var adapter = new SwarmEventAdapter();
        var interceptor = new AgUIEventInterceptor(inner, adapter, "worker-a");

        interceptor.Dispose();

        inner.DisposeCallCount.Should().Be(0, "the inner client is a shared singleton — the interceptor does not own its lifetime");
    }

    private sealed class DisposeTrackingChatClient : IChatClient
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose() => this.DisposeCallCount++;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
