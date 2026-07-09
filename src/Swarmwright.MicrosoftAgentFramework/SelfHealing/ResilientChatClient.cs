using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Swarmwright.MicrosoftAgentFramework.SelfHealing;

/// <summary>
/// A delegating <see cref="IChatClient"/> that retries on HTTP 429 (Too Many Requests)
/// using Polly exponential backoff. Acts as a second-tier safety net after the provider SDK's
/// built-in retry policy has been exhausted.
/// </summary>
public sealed partial class ResilientChatClient : IChatClient
{
    private readonly IChatClient inner;
    private readonly ResiliencePipeline pipeline;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientChatClient"/> class.
    /// </summary>
    /// <param name="inner">The inner chat client to delegate to.</param>
    /// <param name="logger">The logger for retry telemetry.</param>
    /// <param name="maxRetries">Maximum number of Polly retries.</param>
    /// <param name="baseDelaySeconds">Base delay in seconds for exponential backoff.</param>
    public ResilientChatClient(
        IChatClient inner,
        ILogger logger,
        int maxRetries = 3,
        double baseDelaySeconds = 2.0)
    {
        this.inner = inner;
        this.logger = logger;
        this.pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ClientResultException>(ex => ex.Status == 429),
                MaxRetryAttempts = maxRetries,
                DelayGenerator = args =>
                {
                    var delay = GetRetryDelay(args.AttemptNumber, baseDelaySeconds, args.Outcome.Exception);
                    return ValueTask.FromResult<TimeSpan?>(delay);
                },
                OnRetry = args =>
                {
                    this.LogRetryAttempt(args.AttemptNumber + 1, maxRetries, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await this.pipeline.ExecuteAsync(
            async ct => await this.inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.GetStreamingWithRetryAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return this.inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.inner.Dispose();
    }

    /// <summary>
    /// Calculates the retry delay, respecting Retry-After headers when available.
    /// </summary>
    /// <param name="attemptNumber">The current retry attempt number (0-based).</param>
    /// <param name="baseDelaySeconds">Base delay in seconds for exponential backoff.</param>
    /// <param name="exception">The exception that triggered the retry, if any.</param>
    /// <returns>The delay to wait before the next retry.</returns>
    internal static TimeSpan GetRetryDelay(int attemptNumber, double baseDelaySeconds, Exception? exception)
    {
        if (exception is ClientResultException cre)
        {
            var retryAfterDelay = TryGetRetryAfterDelay(cre);
            if (retryAfterDelay.HasValue)
            {
                return retryAfterDelay.Value;
            }
        }

        // Exponential backoff with jitter: base * 2^attempt + random jitter.
        var exponentialDelay = baseDelaySeconds * Math.Pow(2, attemptNumber);
        var maxDelay = Math.Min(exponentialDelay, 60.0);

        // CA5394: Jitter for retry backoff does not require cryptographic randomness.
#pragma warning disable CA5394
        var jitter = Random.Shared.NextDouble() * baseDelaySeconds;
#pragma warning restore CA5394
        return TimeSpan.FromSeconds(maxDelay + jitter);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingWithRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await this.pipeline.ExecuteAsync(
            async ct =>
            {
                // Materialize the streaming response to detect 429 before yielding.
                // If the first call to MoveNextAsync throws, Polly retries.
                var stream = this.inner.GetStreamingResponseAsync(messages, options, ct);
                var enumerator = stream.GetAsyncEnumerator(ct);
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        return (enumerator, first: default(ChatResponseUpdate?), hasFirst: false);
                    }

                    return (enumerator, first: enumerator.Current, hasFirst: true);
                }
                catch
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);

        var (enumerator, first, hasFirst) = response;
        try
        {
            if (hasFirst && first is not null)
            {
                yield return first;
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return enumerator.Current;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TimeSpan? TryGetRetryAfterDelay(ClientResultException cre)
    {
        var rawResponse = cre.GetRawResponse();
        if (rawResponse == null)
        {
            return null;
        }

        if (rawResponse.Headers != null
            && rawResponse.Headers.TryGetValue("Retry-After", out var retryAfterValue)
            && int.TryParse(retryAfterValue, out var retryAfterSeconds)
            && retryAfterSeconds > 0)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        return null;
    }

    [LoggerMessage(LogLevel.Warning, "Polly retry {RetryAttempt}/{MaxRetries} for 429 Too Many Requests. Waiting {DelaySeconds:F1}s.")]
    private partial void LogRetryAttempt(int retryAttempt, int maxRetries, double delaySeconds);
}
