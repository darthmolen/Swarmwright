using Azure.Core;
using Azure.Identity;
using Swarmwright.Configuration;

namespace Swarmwright.Archival;

/// <summary>
/// Builds the Azure <see cref="TokenCredential"/> the blob archiver authenticates
/// with. Environment-driven: Development/Testing default to a service-principal
/// (<see cref="ClientSecretCredential"/>); other environments default to
/// <see cref="ManagedIdentityCredential"/>. An explicit
/// <see cref="SwarmArchivalOptions.CredentialType"/> override wins.
/// <para>
/// The credential switch is a deliberate copy of
/// <c>WorkflowServiceExtensions.BuildFoundryCredential</c> (which is
/// <c>private static</c> in the Workflows project and not callable here); per
/// CLAUDE.md §3 no shared helper is extracted in this slice. Consolidate if a
/// third consumer appears.
/// </para>
/// </summary>
public static class SwarmArchiverCredentialFactory
{
    /// <summary>
    /// Creates the token credential for the given hosting environment and options.
    /// </summary>
    /// <param name="environmentName">The hosting environment name (for example, <c>Development</c>, <c>Testing</c>, <c>Production</c>).</param>
    /// <param name="options">The archival options carrying the optional override and credential material.</param>
    /// <returns>The resolved token credential.</returns>
    public static TokenCredential Create(string environmentName, SwarmArchivalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var credentialType = options.CredentialType ?? DefaultForEnvironment(environmentName);

        return credentialType switch
        {
            SwarmArchivalCredentialType.ManagedIdentity =>
                string.IsNullOrEmpty(options.ManagedIdentityClientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId)),
            SwarmArchivalCredentialType.ClientSecret =>
                new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret),
            SwarmArchivalCredentialType.ClientCertificate =>
                new ClientCertificateCredential(options.TenantId, options.ClientId, options.ClientSecret),
            SwarmArchivalCredentialType.Environment =>
                new EnvironmentCredential(),
            SwarmArchivalCredentialType.Default =>
                new DefaultAzureCredential(),
            _ => throw new InvalidOperationException(
                $"Unsupported swarm-archival credential type: {credentialType}"),
        };
    }

    private static SwarmArchivalCredentialType DefaultForEnvironment(string environmentName)
    {
        return environmentName is "Development" or "Testing"
            ? SwarmArchivalCredentialType.ClientSecret
            : SwarmArchivalCredentialType.ManagedIdentity;
    }
}
