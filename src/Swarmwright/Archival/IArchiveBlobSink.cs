namespace Swarmwright.Archival;

/// <summary>
/// The low-level blob upload seam the <see cref="BlobSwarmRunArchiver"/> writes
/// through. Kept internal and separate from the archiver so upload ordering and
/// idempotency can be unit-tested against a fake without an Azure dependency;
/// the real implementation wraps <c>BlobContainerClient</c> and is
/// integration-tested.
/// </summary>
internal interface IArchiveBlobSink
{
    /// <summary>
    /// Uploads a single blob at <paramref name="relativePath"/> from
    /// <paramref name="content"/>.
    /// </summary>
    /// <param name="relativePath">The blob path relative to the run's container prefix.</param>
    /// <param name="content">The content stream to upload.</param>
    /// <param name="overwrite">Whether to overwrite an existing blob at the path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous upload.</returns>
    public Task UploadAsync(string relativePath, Stream content, bool overwrite, CancellationToken cancellationToken);
}
