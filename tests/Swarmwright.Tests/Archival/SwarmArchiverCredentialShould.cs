using Azure.Identity;
using Swarmwright.Configuration;
using Swarmwright.Archival;
using FluentAssertions;

namespace Swarmwright.Tests.Archival;

/// <summary>
/// Tests for <see cref="SwarmArchiverCredentialFactory"/> — the environment-driven
/// credential switch used by the blob archiver. Mirrors the
/// <c>WorkflowServiceExtensions.BuildFoundryCredential</c> switch over the shared
/// <see cref="SwarmArchivalCredentialType"/> enum.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmArchiverCredentialShould
{
    /// <summary>
    /// Verifies that under the <c>Testing</c> environment (and with no explicit
    /// override) the factory returns a <see cref="ClientSecretCredential"/>.
    /// </summary>
    [TestMethod]
    public void ReturnClientSecretCredentialUnderTesting()
    {
        var options = new SwarmArchivalOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "secret-value",
        };

        var credential = SwarmArchiverCredentialFactory.Create("Testing", options);

        credential.Should().BeOfType<ClientSecretCredential>(
            "Development/Testing environments default to a service-principal credential.");
    }

    /// <summary>
    /// Verifies that under the <c>Production</c> environment (no override) the
    /// factory returns a <see cref="ManagedIdentityCredential"/>.
    /// </summary>
    [TestMethod]
    public void ReturnManagedIdentityCredentialUnderProduction()
    {
        var options = new SwarmArchivalOptions
        {
            ManagedIdentityClientId = "33333333-3333-3333-3333-333333333333",
        };

        var credential = SwarmArchiverCredentialFactory.Create("Production", options);

        credential.Should().BeOfType<ManagedIdentityCredential>(
            "non-Development/Testing environments default to managed identity.");
    }

    /// <summary>
    /// Verifies that an explicit <see cref="SwarmArchivalOptions.CredentialType"/>
    /// override wins over the environment default.
    /// </summary>
    [TestMethod]
    public void HonorExplicitCredentialTypeOverride()
    {
        var options = new SwarmArchivalOptions
        {
            CredentialType = SwarmArchivalCredentialType.ManagedIdentity,
            ManagedIdentityClientId = "44444444-4444-4444-4444-444444444444",
        };

        // Even under Testing (which would otherwise pick ClientSecret), the
        // explicit override must select managed identity.
        var credential = SwarmArchiverCredentialFactory.Create("Testing", options);

        credential.Should().BeOfType<ManagedIdentityCredential>(
            "an explicit CredentialType override must win over the environment default.");
    }
}
