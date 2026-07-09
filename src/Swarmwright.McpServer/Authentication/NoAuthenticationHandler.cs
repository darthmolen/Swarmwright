using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.McpServer.Configuration;

namespace Swarmwright.McpServer.Authentication;

/// <summary>
/// No-auth handler used when <see cref="SwarmMcpAuthMode.None"/> is configured.
/// Unconditionally succeeds and fabricates a principal carrying both Read and Write role claims.
/// Intended for development and integration-test environments only.
/// </summary>
public sealed class NoAuthenticationHandler : AuthenticationHandler<NoAuthenticationOptions>
{
    private readonly SwarmMcpAuthorizationOptions authzOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="options">The options monitor for this handler.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="authzOptions">The authorization options providing role names.</param>
    public NoAuthenticationHandler(
        IOptionsMonitor<NoAuthenticationOptions> options,
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
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, this.authzOptions.ReadRole),
            new Claim(ClaimTypes.Role, this.authzOptions.WriteRole),
            new Claim(ClaimTypes.NameIdentifier, "swarm-mcp-anonymous"),
            new Claim(ClaimTypes.Name, "swarm-mcp-anonymous"),
        };
        var identity = new ClaimsIdentity(claims, NoAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, NoAuthenticationOptions.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
