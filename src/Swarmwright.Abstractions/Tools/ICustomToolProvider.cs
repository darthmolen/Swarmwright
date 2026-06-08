using Microsoft.Extensions.AI;

namespace Swarmwright.Tools;

/// <summary>
/// Supplies custom domain-specific <see cref="AITool"/> instances that workers can opt
/// into via their <c>custom_tools:</c> frontmatter list. Consumers typically subclass
/// <c>CustomToolProvider</c> and decorate methods with <see cref="SwarmToolAttribute"/>
/// rather than implementing this interface directly.
/// </summary>
public interface ICustomToolProvider
{
    /// <summary>Returns the tools supplied by this provider.</summary>
    /// <returns>The list of tools.</returns>
    public IReadOnlyList<AITool> GetTools();
}
