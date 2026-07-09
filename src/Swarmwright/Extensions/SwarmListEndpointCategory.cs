namespace Swarmwright.Extensions;

/// <summary>
/// Marker category class used as the generic type parameter for
/// <c>ILogger&lt;TCategoryName&gt;</c> in the swarm list endpoint. A dedicated
/// non-static type is required because the static <see cref="SwarmListFallbackLogger"/>
/// partial helper cannot be used as a generic type argument. This class has
/// no members of its own and exists solely to carry the log category name.
/// </summary>
internal sealed class SwarmListEndpointCategory
{
}
