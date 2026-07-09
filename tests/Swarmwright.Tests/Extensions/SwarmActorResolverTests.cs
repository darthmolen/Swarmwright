using System.Security.Claims;
using Swarmwright.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Swarmwright.Tests.Extensions;

/// <summary>
/// Tests for <see cref="SwarmActorResolver"/>. The resolver decides the actor
/// string written to <c>SwarmStateTransition.Actor</c> / <c>SwarmEntity.LockedBy</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwarmActorResolverTests
{
    [TestMethod]
    public void Resolve_WithAuthenticatedPrincipal_ReturnsPrincipalName()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice@corp.com")],
            authenticationType: "Bearer");
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };

        SwarmActorResolver.Resolve(ctx).Should().Be("alice@corp.com");
    }

    [TestMethod]
    public void Resolve_WithoutPrincipal_ButWithActorHeader_ReturnsHeaderValue()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[SwarmActorResolver.ActorHeader] = "bob@dev.local";

        SwarmActorResolver.Resolve(ctx).Should().Be("bob@dev.local");
    }

    [TestMethod]
    public void Resolve_PrincipalWinsOverHeader()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "alice@corp.com")],
                authenticationType: "Bearer")),
        };
        ctx.Request.Headers[SwarmActorResolver.ActorHeader] = "bob@dev.local";

        SwarmActorResolver.Resolve(ctx).Should().Be("alice@corp.com");
    }

    [TestMethod]
    public void Resolve_NoIdentityNoHeader_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();

        SwarmActorResolver.Resolve(ctx).Should().BeNull();
    }

    [TestMethod]
    public void Resolve_NullContext_ReturnsNull()
    {
        SwarmActorResolver.Resolve(null).Should().BeNull();
    }

    [TestMethod]
    public void Resolve_EmptyHeaderValue_IsIgnored()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[SwarmActorResolver.ActorHeader] = string.Empty;

        SwarmActorResolver.Resolve(ctx).Should().BeNull();
    }
}
