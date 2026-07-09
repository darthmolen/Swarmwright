namespace Swarmwright.Tools;

/// <summary>
/// Marks a method on a <c>CustomToolProvider</c> subclass as an AI tool exposed
/// to swarm workers. The explicit <see cref="Name"/> and <see cref="Description"/>
/// are required so tool identity is stable under method renames and so the
/// description shown to the model is rich enough to be useful.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SwarmToolAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmToolAttribute"/> class.
    /// </summary>
    /// <param name="name">The tool name exposed to the LLM (e.g. <c>"query_db"</c>).</param>
    /// <param name="description">The tool description shown to the LLM.</param>
    public SwarmToolAttribute(string name, string description)
    {
        this.Name = name;
        this.Description = description;
    }

    /// <summary>Gets the tool name exposed to the LLM.</summary>
    public string Name { get; }

    /// <summary>Gets the tool description shown to the LLM.</summary>
    public string Description { get; }
}
