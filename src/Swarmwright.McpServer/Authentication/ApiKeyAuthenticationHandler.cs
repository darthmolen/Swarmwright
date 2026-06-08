using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.McpServer.Configuration;

namespace Swarmwright.McpServer.Authentication;

/// <summary>
/// Authentication handler that validates a shared API key supplied via the
/// <c>X-API-Key</c> request header. On success it synthesizes a principal
/// carrying both the Read and Write role claims so the Swarm MCP authorization
/// policies succeed for the caller.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly SwarmMcpAuthorizationOptions authzOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="options">The options monitor for this handler.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="authzOptions">The authorization options providing role names.</param>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<SwarmMcpAuthorizationOptions> authzOptions)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(authzOptions);
        this.authzOptions = authzOptions.Value;
    }

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!this.Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var supplied)
            || string.IsNullOrEmpty(supplied))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = this.Options.ExpectedApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Swarm MCP API key authentication is enabled but no ExpectedApiKey is configured."));
        }

        if (!FixedTimeEquals(supplied.ToString(), expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Role, this.authzOptions.ReadRole),
            new Claim(ClaimTypes.Role, this.authzOptions.WriteRole),
            new Claim(ClaimTypes.NameIdentifier, "swarm-mcp-apikey"),
            new Claim(ClaimTypes.Name, "swarm-mcp-apikey"),
        };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
