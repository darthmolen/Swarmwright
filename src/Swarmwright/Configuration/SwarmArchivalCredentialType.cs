namespace Swarmwright.Configuration;

/// <summary>
/// Selects which Azure <c>TokenCredential</c> the swarm-run blob archiver authenticates with.
/// Replaces the CSAT <c>FoundryCredentialType</c>; each value maps directly to an
/// <c>Azure.Identity</c> credential type in <see cref="Archival.SwarmArchiverCredentialFactory"/>.
/// </summary>
public enum SwarmArchivalCredentialType
{
    /// <summary>Use <c>DefaultAzureCredential</c> (chained credential discovery).</summary>
    Default = 0,

    /// <summary>Use <c>ManagedIdentityCredential</c> (system- or user-assigned).</summary>
    ManagedIdentity = 1,

    /// <summary>Use <c>ClientSecretCredential</c> (service principal with secret).</summary>
    ClientSecret = 2,

    /// <summary>Use <c>ClientCertificateCredential</c> (service principal with certificate).</summary>
    ClientCertificate = 3,

    /// <summary>Use <c>EnvironmentCredential</c> (credentials from environment variables).</summary>
    Environment = 4,
}
