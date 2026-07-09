using Microsoft.Extensions.Configuration;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for <see cref="IConfigurationBuilder"/> that support
/// auto-discovery of configuration contributed by standalone swarm template
/// NuGet packages.
/// </summary>
/// <remarks>
/// Each swarm template package ships an <c>appsettings.swarm-{template-key}.json</c>
/// sidecar as a NuGet content file. These sidecars are copied alongside the
/// consumer's own <c>appsettings.json</c> at build time. Calling
/// <see cref="AddSwarmTemplatePackages"/> during host setup globs the content
/// root for those files and layers each one into the configuration pipeline,
/// so the MCP endpoints, agent defaults, or other settings a template package
/// needs are contributed automatically when the package is installed.
/// </remarks>
public static class ConfigurationBuilderExtensions
{
    private const string BaseFilePattern = "appsettings.swarm-*.json";
    private const string FileNamePrefix = "appsettings.";

    /// <summary>
    /// Probes the content root for <c>appsettings.swarm-*.json</c> sidecar files
    /// shipped by swarm template NuGet packages and layers each one into the
    /// configuration pipeline. Also loads environment-specific variants
    /// (<c>appsettings.swarm-{key}.{Environment}.json</c>) after each base file
    /// if the <c>DOTNET_ENVIRONMENT</c> or <c>ASPNETCORE_ENVIRONMENT</c>
    /// variable is set, mirroring the stock <c>appsettings.{Environment}.json</c>
    /// convention.
    /// </summary>
    /// <param name="builder">The configuration builder to extend.</param>
    /// <param name="contentRootPath">
    /// The directory to scan. When <c>null</c>, defaults to
    /// <see cref="AppContext.BaseDirectory"/> (the consumer's build output folder).
    /// </param>
    /// <returns>The builder for chaining.</returns>
    public static IConfigurationBuilder AddSwarmTemplatePackages(
        this IConfigurationBuilder builder,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var root = contentRootPath ?? AppContext.BaseDirectory;
        if (!Directory.Exists(root))
        {
            return builder;
        }

        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        // Base files follow the shape "appsettings.swarm-<key>.json" — three
        // dot-separated segments. Environment variants have four
        // ("appsettings.swarm-<key>.<Env>.json") and are layered per-base below.
        var baseFiles = Directory.GetFiles(root, BaseFilePattern)
            .Where(path => Path.GetFileName(path).Split('.').Length == 3)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var baseFile in baseFiles)
        {
            builder.AddJsonFile(baseFile, optional: true, reloadOnChange: true);

            if (!string.IsNullOrEmpty(environment))
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(baseFile);
                var templateKeySegment = fileNameWithoutExtension[FileNamePrefix.Length..];
                var envFile = Path.Combine(
                    root,
                    $"{FileNamePrefix}{templateKeySegment}.{environment}.json");

                if (File.Exists(envFile))
                {
                    builder.AddJsonFile(envFile, optional: true, reloadOnChange: true);
                }
            }
        }

        if (baseFiles.Length > 0)
        {
            // Pre-logger-startup breadcrumb so ops can see what got loaded.
            var discovered = string.Join(", ", baseFiles.Select(Path.GetFileName));
            Console.WriteLine(
                $"[Swarmwright] Loaded {baseFiles.Length} swarm template package config(s): {discovered}");
        }

        return builder;
    }
}
