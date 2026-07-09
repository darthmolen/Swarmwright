using Microsoft.AspNetCore.Authentication;

namespace Swarmwright.McpServer.Authentication;

/// <summary>
/// Options for the <see cref="NoAuthenticationHandler"/>. No configurable state.
/// </summary>
public sealed class NoAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The scheme name used to register this handler.</summary>
    public const string SchemeName = "SwarmMcpNoAuth";
}
