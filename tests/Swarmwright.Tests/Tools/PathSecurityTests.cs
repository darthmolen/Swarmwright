using Swarmwright.Tools;
using FluentAssertions;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Dedicated tests for <see cref="PathSecurity.TryResolveSafePath"/>. This helper is the
/// only defense between LLM-supplied path strings (from the <c>read</c>, <c>write</c>,
/// and related work-directory-scoped tools) and the filesystem — the tests here pin its
/// behavior on each rejection case so future refactors cannot silently open a traversal.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PathSecurityTests
{
    private readonly string workDir = Path.Combine(Path.GetTempPath(), "pathsec-tests-" + Guid.NewGuid().ToString("N"));

    // ---- Acceptance cases ----

    [TestMethod]
    public void TryResolveSafePath_AcceptsSimpleRelativeFilename()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "notes.md", out var resolved).Should().BeTrue();
        resolved.Should().Be(Path.Combine(Path.GetFullPath(this.workDir), "notes.md"));
    }

    [TestMethod]
    public void TryResolveSafePath_AcceptsNestedRelativePath()
    {
        PathSecurity.TryResolveSafePath(this.workDir, Path.Combine("subdir", "notes.md"), out var resolved).Should().BeTrue();
        resolved.Should().Contain("subdir");
    }

    [TestMethod]
    public void TryResolveSafePath_AcceptsForwardSlashSeparators()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "subdir/notes.md", out var resolved).Should().BeTrue();
        resolved.Should().Contain("notes.md");
    }

    // ---- Rejection: absolute paths ----

    [TestMethod]
    public void TryResolveSafePath_RejectsAbsoluteWindowsPath()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "C:\\Windows\\System32\\config", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveSafePath_RejectsAbsoluteUnixPath()
    {
        // Path.IsPathRooted returns true for paths starting with "/" on Windows too.
        PathSecurity.TryResolveSafePath(this.workDir, "/etc/passwd", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveSafePath_RejectsUncPath()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "\\\\server\\share\\file", out _).Should().BeFalse();
    }

    // ---- Rejection: traversal ----

    [TestMethod]
    public void TryResolveSafePath_RejectsTraversal_OneLevel()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "../escape.txt", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveSafePath_RejectsTraversal_MultiLevel()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "../../../../etc/passwd", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveSafePath_RejectsTraversalMixedWithValid()
    {
        // Traversal embedded in the middle of a path — after canonicalization the
        // resolved path escapes the work directory.
        PathSecurity.TryResolveSafePath(this.workDir, Path.Combine("subdir", "..", "..", "escape.txt"), out _).Should().BeFalse();
    }

    // ---- Rejection: empty / null ----

    [TestMethod]
    public void TryResolveSafePath_RejectsEmptyString()
    {
        PathSecurity.TryResolveSafePath(this.workDir, string.Empty, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveSafePath_RejectsWhitespace()
    {
        PathSecurity.TryResolveSafePath(this.workDir, "   ", out _).Should().BeFalse();
    }
}
