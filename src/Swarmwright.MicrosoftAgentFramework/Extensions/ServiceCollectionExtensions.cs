using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenAI;
using Swarmwright.MicrosoftAgentFramework.Configuration;
using Swarmwright.MicrosoftAgentFramework.SelfHealing;

namespace Swarmwright.MicrosoftAgentFramework.Extensions;

/// <summary>
/// Extension methods for registering the shared <see cref="IChatClient"/> that Swarmwright agents
/// consume. Two providers are supported: Azure OpenAI (<see cref="AddSwarmwrightAzureOpenAI"/>) and
/// any OpenAI-compatible endpoint such as vLLM or Ollama (<see cref="AddSwarmwrightOpenAI"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IChatClient"/> backed by <see cref="AzureOpenAIClient"/>
    /// wrapped in <see cref="ResilientChatClient"/>. Idempotent via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>.
    /// Reads the top-level <c>AzureOpenAI</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmwrightAzureOpenAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>()
            ?? throw new InvalidOperationException(
                $"AzureOpenAI configuration section '{AzureOpenAIOptions.SectionName}' is missing.");

        // Eager validation. Get<T>() succeeds for a present-but-empty section (returns defaults),
        // so a friendly error here is the difference between a clear startup failure and an opaque
        // UriFormatException surfaced lazily from inside the DI factory on first resolution.
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException(
                $"{AzureOpenAIOptions.SectionName}:Endpoint is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"{AzureOpenAIOptions.SectionName}:ApiKey is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DeploymentName))
        {
            throw new InvalidOperationException(
                $"{AzureOpenAIOptions.SectionName}:DeploymentName is required.");
        }

        services.TryAddSingleton<IChatClient>(sp =>
        {
            var clientOptions = BuildClientOptions(options);
            var azureClient = new AzureOpenAIClient(
                new Uri(options.Endpoint),
                new ApiKeyCredential(options.ApiKey),
                clientOptions);

            // UseBackgroundResponses=true switches the chat client onto the OpenAI Responses API
            // pipeline (background-mode runs + resume via continuation token). AzureOpenAIClient
            // derives from OpenAIClient; GetResponsesClient takes no deployment argument — the
            // deployment is supplied to the AsIChatClient adapter instead.
            IChatClient inner;
            if (options.UseBackgroundResponses)
            {
                inner = azureClient.GetResponsesClient()
                    .AsIChatClient(options.DeploymentName)
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
            else
            {
                inner = azureClient.GetChatClient(options.DeploymentName)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientChatClient>();
            return new ResilientChatClient(
                inner,
                logger,
                options.MaxPollyRetries,
                options.RetryBaseDelaySeconds);
        });

        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="IChatClient"/> backed by any OpenAI-compatible endpoint
    /// (vLLM, Ollama, LM Studio, or OpenAI itself) wrapped in <see cref="ResilientChatClient"/>.
    /// Idempotent. The endpoint must expose the OpenAI <c>/v1</c> surface; many local servers
    /// ignore the API key, so it defaults to a non-empty placeholder when omitted.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The OpenAI-compatible base endpoint (e.g. <c>http://localhost:8000/v1</c>).</param>
    /// <param name="model">The served model name (e.g. <c>Qwen/Qwen2.5-7B-Instruct</c>).</param>
    /// <param name="apiKey">The API key; defaults to a placeholder for servers that do not require one.</param>
    /// <param name="maxPollyRetries">Maximum Polly 429 retries. Default 3.</param>
    /// <param name="retryBaseDelaySeconds">Polly exponential-backoff base delay in seconds. Default 2.0.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmwrightOpenAI(
        this IServiceCollection services,
        string endpoint,
        string model,
        string? apiKey = null,
        int maxPollyRetries = 3,
        double retryBaseDelaySeconds = 2.0)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        services.TryAddSingleton<IChatClient>(sp =>
        {
            var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "swarmwright" : apiKey);
            var client = new OpenAIClient(
                credential,
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            var inner = client.GetChatClient(model)
                .AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientChatClient>();
            return new ResilientChatClient(inner, logger, maxPollyRetries, retryBaseDelaySeconds);
        });

        return services;
    }

    /// <summary>
    /// Builds the <see cref="AzureOpenAIClientOptions"/> from the supplied options.
    /// Internal so tests can assert the configured network timeout and retry policy
    /// without resolving a full DI container.
    /// </summary>
    /// <param name="options">The Azure OpenAI options.</param>
    /// <returns>A configured <see cref="AzureOpenAIClientOptions"/>.</returns>
    internal static AzureOpenAIClientOptions BuildClientOptions(AzureOpenAIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var networkTimeoutSeconds = Math.Max(1, options.NetworkTimeoutSeconds);
        return new AzureOpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(options.MaxLlmRetries),
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
        };
    }
}
