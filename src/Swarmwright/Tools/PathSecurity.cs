namespace Swarmwright.Tools;

/// <summary>
/// Helpers for confining file paths to a work directory and rejecting traversal attempts.
/// </summary>
public static class PathSecurity
{
    /// <summary>
    /// Resolves a relative path against a work directory and verifies it stays within it.
    /// Rejects absolute paths, parent traversal (<c>..</c>), and any path that escapes
    /// the work directory after canonicalization.
    /// </summary>
    /// <param name="workDirectory">The work directory the path must remain inside.</param>
    /// <param name="relativePath">The relative path supplied by the agent.</param>
    /// <param name="resolvedFullPath">The canonicalized full path if the resolution succeeds.</param>
    /// <returns><c>true</c> if the path is safely confined; otherwise <c>false</c>.</returns>
    public static bool TryResolveSafePath(
        string workDirectory,
        string relativePath,
        out string resolvedFullPath)
    {
        resolvedFullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // Reject Windows drive-letter (C:\) and UNC (\\server) paths explicitly so the security
        // boundary is identical on every OS rather than dependent on Path.IsPathRooted's
        // platform-specific recognition (on Linux those forms are not considered rooted).
        if (IsWindowsDriveRooted(relativePath) || IsUncPath(relativePath))
        {
            return false;
        }

        // Reject absolute paths outright (POSIX-rooted on Linux; drive/UNC handled above on Linux,
        // and by this check on Windows).
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var workDirFull = Path.GetFullPath(workDirectory);
        var combined = Path.GetFullPath(Path.Combine(workDirFull, relativePath));

        // After canonicalization, the resolved path must still be within the work directory.
        var workDirWithSep = workDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? workDirFull
            : workDirFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(workDirWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, workDirFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolvedFullPath = combined;
        return true;
    }

    private static bool IsWindowsDriveRooted(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
