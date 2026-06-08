namespace Swarmwright.Configuration;

/// <summary>
/// Configuration for promoting a completed swarm-run work directory to durable
/// blob storage. Bound from <c>Swarm:Archival</c>. Opt-in per host via
/// <see cref="Enabled"/>; the sink is implicitly Azure Blob in v1.
/// </summary>
public sealed class SwarmArchivalOptions
{
    /// <summary>Gets or sets a value indicating whether run archival is enabled. Off by default; the only toggle in v1.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the blob container URI archives are written to (for example, <c>https://acct.blob.core.windows.net/swarm-corpus</c>).</summary>
    public Uri? ContainerUri { get; set; }

    /// <summary>
    /// Gets or sets the explicit credential type override. When <c>null</c>, the
    /// environment default applies — <see cref="SwarmArchivalCredentialType.ClientSecret"/>
    /// in Development/Testing, <see cref="SwarmArchivalCredentialType.ManagedIdentity"/> otherwise.
    /// </summary>
    public SwarmArchivalCredentialType? CredentialType { get; set; }

    /// <summary>Gets or sets the user-assigned managed-identity client id. When empty, system-assigned identity is used.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Gets or sets the tenant id for the service-principal credential used in Development/Testing.</summary>
    public string? TenantId { get; set; }

    /// <summary>Gets or sets the client id for the service-principal credential used in Development/Testing.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the client secret for the service-principal credential used in Development/Testing.</summary>
    public string? ClientSecret { get; set; }
}
