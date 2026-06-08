using System.IO.Compression;
using Swarmwright.Tools;

namespace Swarmwright.Extensions;

/// <summary>
/// Helpers for reading files out of a swarm's work directory for the artifact endpoints.
/// </summary>
internal static class ArtifactProvider
{
    /// <summary>
    /// Maximum number of bytes to sniff when deciding whether a file is text or binary.
    /// </summary>
    private const int TextSniffBytes = 1024;

    /// <summary>
    /// Enumerates every file under the work directory and returns its relative path and byte size.
    /// </summary>
    /// <param name="workDirectory">The work directory to enumerate.</param>
    /// <returns>A list of <see cref="ArtifactEntry"/> records, one per file.</returns>
    public static IReadOnlyList<ArtifactEntry> ListArtifacts(string workDirectory)
    {
        var results = new List<ArtifactEntry>();

        foreach (var fullPath in Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories))
        {
            if (!PathSecurity.TryResolveSafePath(
                workDirectory,
                Path.GetRelativePath(workDirectory, fullPath),
                out var resolved))
            {
                continue;
            }

            var info = new FileInfo(resolved);
            if (!info.Exists)
            {
                continue;
            }

            var relative = Path.GetRelativePath(workDirectory, resolved).Replace('\\', '/');
            results.Add(new ArtifactEntry(
                Name: Path.GetFileName(relative),
                Path: relative,
                Size: info.Length));
        }

        return results;
    }

    /// <summary>
    /// Builds a ZIP archive containing every file under <paramref name="workDirectory"/> that
    /// passes <see cref="PathSecurity.TryResolveSafePath"/> validation. Symlinks that escape
    /// the work directory are silently skipped.
    /// </summary>
    /// <param name="workDirectory">The work directory to archive.</param>
    /// <returns>A memory stream positioned at the beginning of the ZIP payload.</returns>
    public static MemoryStream CreateZipArchive(string workDirectory)
    {
        var memoryStream = new MemoryStream();

        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fullPath in Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(workDirectory, fullPath);

                if (!PathSecurity.TryResolveSafePath(workDirectory, relative, out var resolved))
                {
                    continue;
                }

                if (!File.Exists(resolved))
                {
                    continue;
                }

                var entryName = relative.Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(resolved);
                fileStream.CopyTo(entryStream);
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <summary>
    /// Heuristically determines whether the file at <paramref name="fullPath"/> should be served
    /// as <c>text/plain</c> (small, printable UTF-8) or <c>application/octet-stream</c>.
    /// </summary>
    /// <param name="fullPath">The absolute path to the file.</param>
    /// <returns><c>true</c> if the file appears to be printable text; otherwise <c>false</c>.</returns>
    public static bool LooksLikeText(string fullPath)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            Span<byte> buffer = stackalloc byte[TextSniffBytes];
            var read = fs.Read(buffer);
            if (read == 0)
            {
                return true;
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];

                // Allow common whitespace.
                if (b is 0x09 or 0x0A or 0x0D)
                {
                    continue;
                }

                // Anything below space (except whitespace above) or a NUL byte is binary.
                if (b < 0x20)
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
