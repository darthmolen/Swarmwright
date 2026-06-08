namespace Swarmwright.MicrosoftAgentFramework.Configuration;

/// <summary>
/// Configuration options for the shared Azure OpenAI <c>IChatClient</c> registration.
/// Bound from the top-level <c>AzureOpenAI</c> configuration section.
/// </summary>
public class AzureOpenAIOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "AzureOpenAI";

    /// <summary>Gets or sets the Azure OpenAI endpoint URI (e.g. <c>https://my-resource.openai.azure.com/</c>).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the API key used to authenticate to the Azure OpenAI endpoint.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Azure OpenAI deployment name to invoke (e.g. <c>gpt-4o</c>). Required —
    /// <c>AddSwarmwrightAzureOpenAI</c> throws <c>InvalidOperationException</c> at registration time
    /// if left empty rather than silently defaulting to a model that may not exist in the
    /// operator's tenant.
    /// </summary>
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the per-request network timeout for the Azure OpenAI SDK pipeline, in seconds.
    /// Maps to <c>AzureOpenAIClientOptions.NetworkTimeout</c>. Default 600 (10 minutes). The
    /// SDK's own default is 100s, which is frequently too short for tool-call responses on
    /// large inputs.
    /// </summary>
    public int NetworkTimeoutSeconds { get; set; } = 600;

    /// <summary>Gets or sets the SDK-layer retry count via <c>ClientRetryPolicy</c>. Default 6.</summary>
    public int MaxLlmRetries { get; set; } = 6;

    /// <summary>Gets or sets the Polly-layer retry count in <c>ResilientChatClient</c>. Default 3.</summary>
    public int MaxPollyRetries { get; set; } = 3;

    /// <summary>Gets or sets the Polly exponential-backoff base delay in seconds. Default 2.0.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets a value indicating whether the shared <c>IChatClient</c> is wired against
    /// the OpenAI Responses API (instead of Chat Completions), enabling background-mode runs
    /// and resume via continuation token. Default <see langword="false"/>.
    /// </summary>
    public bool UseBackgroundResponses { get; set; }
}
