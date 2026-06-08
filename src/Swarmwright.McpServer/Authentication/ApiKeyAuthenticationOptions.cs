using Microsoft.AspNetCore.Authentication;

namespace Swarmwright.McpServer.Authentication;

/// <summary>
/// Options for the <see cref="ApiKeyAuthenticationHandler"/>.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The scheme name used to register this handler.</summary>
    public const string SchemeName = "SwarmMcpApiKey";

    /// <summary>The HTTP request header carrying the API key.</summary>
    public const string HeaderName = "X-API-Key";

    /// <summary>
    /// Gets or sets the expected API key value. Compared in constant time to the supplied header.
    /// </summary>
    public string ExpectedApiKey { get; set; } = string.Empty;
}
