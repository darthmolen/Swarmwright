using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarmwright.Events;

namespace Swarmwright.Archival;

/// <summary>
/// Copies a completed swarm-run work directory verbatim to a blob container via
/// the injected <see cref="IArchiveBlobSink"/>. Uploads every tree artifact
/// first, then writes the run <c>manifest.json</c> LAST so its presence marks a
/// complete archive. All uploads are <c>overwrite</c> so a re-archive of the
/// same run self-heals a partial prior attempt.
/// </summary>
public sealed partial class BlobSwarmRunArchiver : ISwarmRunArchiver
{
    private const string ManifestBlobName = "manifest.json";

    private readonly IArchiveBlobSink sink;
    private readonly ILogger<BlobSwarmRunArchiver> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobSwarmRunArchiver"/> class.
    /// </summary>
    /// <param name="sink">The blob upload sink.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    internal BlobSwarmRunArchiver(IArchiveBlobSink sink, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.sink = sink;
        this.logger = loggerFactory.CreateLogger<BlobSwarmRunArchiver>();
    }

    /// <inheritdoc/>
    public async Task ArchiveAsync(SwarmRunArchiveContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var root = context.WorkDirectory;
        if (!Directory.Exists(root))
        {
            this.LogWorkDirectoryMissing(context.SwarmId, root);
            return;
        }

        var fileCount = 0;
        long byteCount = 0;

        // Upload the tree first; the manifest is written last as the
        // completeness marker. The manifest is excluded from the tree walk to
        // avoid uploading a stale copy ahead of the freshly built one.
        foreach (var absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, absolutePath));
            if (string.Equals(relativePath, ManifestBlobName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            await this.sink.UploadAsync(relativePath, stream, overwrite: true, cancellationToken).ConfigureAwait(false);
            fileCount++;
            byteCount += stream.Length;
        }

        var manifestJson = BuildManifestJson(context, root);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await using (var manifestStream = new MemoryStream(manifestBytes))
        {
            await this.sink.UploadAsync(ManifestBlobName, manifestStream, overwrite: true, cancellationToken).ConfigureAwait(false);
        }

        this.LogRunArchived(context.SwarmId, fileCount, byteCount);
    }

    private static string BuildManifestJson(SwarmRunArchiveContext context, string workDirectory)
    {
        var manifest = new
        {
            swarmId = context.SwarmId.ToString(),
            goal = context.Goal,
            templateKey = context.TemplateKey,
            finalState = context.FinalState,
            failureReason = context.FailureReason,
            createdUtc = context.CreatedUtc,
            completedUtc = context.CompletedUtc,
            agents = ReadRoster(workDirectory),

            // The per-swarm run-context bag (Feature-1 ISwarmRunContext values,
            // e.g. downstream PR/slice pointers) supplied at swarm creation.
            // An empty object when no context was supplied.
            context = context.Context,
        };

        return JsonSerializer.Serialize(manifest, SwarmJsonOptions.Default);
    }

    private static JsonElement ReadRoster(string workDirectory)
    {
        var rosterPath = Path.Combine(workDirectory, ".chat", "agents.json");
        if (!File.Exists(rosterPath))
        {
            using var empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }

        var rosterJson = File.ReadAllText(rosterPath);
        using var doc = JsonDocument.Parse(rosterJson);
        return doc.RootElement.Clone();
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    [LoggerMessage(LogLevel.Warning, "Swarm {SwarmId}: archival work directory '{WorkDirectory}' does not exist; nothing to archive.")]
    private partial void LogWorkDirectoryMissing(Guid swarmId, string workDirectory);

    [LoggerMessage(LogLevel.Information, "Swarm {SwarmId} archived: {FileCount} files, {ByteCount} bytes (manifest last).")]
    private partial void LogRunArchived(Guid swarmId, int fileCount, long byteCount);
}
