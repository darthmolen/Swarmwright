using System.Diagnostics.CodeAnalysis;

namespace Swarmwright.Configuration;

/// <summary>
/// Authentication settings the React admin SPA fetches anonymously from
/// <c>GET /api/spa-config</c> at startup to configure MSAL.js. Bound from the
/// <see cref="SectionName"/> configuration section.
/// </summary>
public sealed class SpaConfiguration
{
    /// <summary>
    /// The configuration section name these options bind from.
    /// </summary>
    public const string SectionName = "SpaConfiguration";

    /// <summary>
    /// Gets or sets the Entra ID (Azure AD) client ID the SPA signs in with.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Entra ID (Azure AD) tenant ID the SPA authenticates against.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default API scope requested when no explicit
    /// <see cref="RequiredPermissions"/> are configured (typically the
    /// <c>api://&lt;client-id&gt;/.default</c> scope).
    /// </summary>
    public string DefaultScope { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fully-qualified delegated scopes the SPA requests on the
    /// access token (for example <c>api://&lt;client-id&gt;/Swarm.Read</c>).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Simple configuration binding from appsettings/user-secrets.")]
    public string[] RequiredPermissions { get; set; } = [];
}
