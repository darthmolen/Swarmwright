namespace Swarmwright.Templates;

/// <summary>
/// Defines a loader for swarm team templates from the file system.
/// </summary>
public interface ITemplateLoader
{
    /// <summary>Gets the root templates directory path.</summary>
    public string TemplatesDirectory { get; }

    /// <summary>
    /// Loads a template by its unique key.
    /// </summary>
    /// <param name="templateKey">The template key corresponding to a subdirectory name.</param>
    /// <returns>The fully loaded template.</returns>
    public LoadedTemplate Load(string templateKey);

    /// <summary>
    /// Loads all available templates from the templates directory.
    /// </summary>
    /// <returns>A read-only list of all loaded templates.</returns>
    public IReadOnlyList<LoadedTemplate> LoadAll();

    /// <summary>
    /// Lists all available template keys.
    /// </summary>
    /// <returns>A read-only list of template key strings.</returns>
    public IReadOnlyList<string> ListAvailable();
}
