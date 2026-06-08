using Azure.Storage.Blobs;

namespace Swarmwright.Archival;

/// <summary>
/// The production <see cref="IArchiveBlobSink"/> that writes through an Azure
/// <see cref="BlobContainerClient"/>. Integration-tested only — unit tests use a
/// fake sink so no Azure dependency is touched. Relies on the Azure SDK's
/// built-in transient retry; no bespoke retry loop in v1.
/// </summary>
internal sealed class BlobArchiveSink : IArchiveBlobSink
{
    private readonly BlobContainerClient containerClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobArchiveSink"/> class.
    /// </summary>
    /// <param name="containerClient">The blob container client archives are written to.</param>
    public BlobArchiveSink(BlobContainerClient containerClient)
    {
        ArgumentNullException.ThrowIfNull(containerClient);
        this.containerClient = containerClient;
    }

    /// <inheritdoc/>
    public async Task UploadAsync(string relativePath, Stream content, bool overwrite, CancellationToken cancellationToken)
    {
        var blobClient = this.containerClient.GetBlobClient(relativePath);
        await blobClient.UploadAsync(content, overwrite, cancellationToken).ConfigureAwait(false);
    }
}
