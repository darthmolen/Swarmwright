using System.Collections.Concurrent;
using Swarmwright.Archival;
using Swarmwright.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Swarmwright.Tests.Archival;

/// <summary>
/// Tests for <see cref="BlobSwarmRunArchiver"/> upload ordering and idempotency,
/// driven against a fake <see cref="IArchiveBlobSink"/> so no Azure dependency is
/// touched in unit tests.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BlobSwarmRunArchiverShould : IDisposable
{
    private readonly string workDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobSwarmRunArchiverShould"/> class.
    /// </summary>
    public BlobSwarmRunArchiverShould()
    {
        this.workDir = Path.Combine(Path.GetTempPath(), "blob-archiver-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workDir);
    }

    /// <summary>
    /// Disposes the temporary work directory.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.workDir))
        {
            Directory.Delete(this.workDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that every file in the work-directory tree is uploaded, that
    /// <c>manifest.json</c> is the LAST upload (the completeness marker), and
    /// that every upload uses <c>overwrite: true</c> for idempotent re-archive.
    /// </summary>
    [TestMethod]
    public async Task UploadWholeTree_ManifestLast_AlwaysOverwrite()
    {
        // Arrange — a small tree with nested artifacts.
        await File.WriteAllTextAsync(Path.Combine(this.workDir, "synthesis-report.md"), "report");
        var chatDir = Path.Combine(this.workDir, ".chat");
        Directory.CreateDirectory(chatDir);
        await File.WriteAllTextAsync(Path.Combine(chatDir, "worker-a.jsonl"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), "[]");

        var sink = new RecordingBlobSink();
        var archiver = new BlobSwarmRunArchiver(sink, NullLoggerFactory.Instance);

        var ctx = new SwarmRunArchiveContext(
            Guid.NewGuid(),
            this.workDir,
            "goal",
            "templateKey",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            SwarmInstanceState.Complete,
            null,
            new Dictionary<string, string>());

        // Act
        await archiver.ArchiveAsync(ctx, CancellationToken.None);

        // Assert — all three tree files plus the manifest were uploaded.
        var paths = sink.Uploads.Select(u => u.RelativePath.Replace('\\', '/')).ToList();
        paths.Should().Contain("synthesis-report.md");
        paths.Should().Contain(".chat/worker-a.jsonl");
        paths.Should().Contain(".chat/agents.json");
        paths.Should().Contain("manifest.json");

        // Manifest is uploaded LAST.
        paths[^1].Should().Be("manifest.json", "the manifest must be the last upload — the completeness marker.");

        // Every upload is overwrite:true.
        sink.Uploads.Should().OnlyContain(u => u.Overwrite, "every upload must be idempotent (overwrite:true).");
    }

    /// <summary>
    /// Verifies the uploaded <c>manifest.json</c> carries the run metadata
    /// (swarmId/goal/templateKey/finalState/failureReason/createdUtc/completedUtc)
    /// and the agents roster read from the work directory's
    /// <c>.chat/agents.json</c>; and that the <c>context</c> field is an empty
    /// object when no run-context was supplied.
    /// </summary>
    [TestMethod]
    public async Task ManifestCarriesRunMetadataAndRoster()
    {
        // Arrange — a work dir with the flushed roster.
        var chatDir = Path.Combine(this.workDir, ".chat");
        Directory.CreateDirectory(chatDir);
        var roster = "[{\"name\":\"security-reviewer-1\",\"role\":\"security-reviewer\",\"displayName\":\"Security\"}]";
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), roster);

        var sink = new RecordingBlobSink();
        var archiver = new BlobSwarmRunArchiver(sink, NullLoggerFactory.Instance);

        var swarmId = Guid.NewGuid();
        var createdUtc = DateTime.UtcNow.AddMinutes(-5);
        var completedUtc = DateTime.UtcNow;
        var ctx = new SwarmRunArchiveContext(
            swarmId,
            this.workDir,
            "Audit the system.",
            "code-review",
            createdUtc,
            completedUtc,
            SwarmInstanceState.Failed,
            "boom",
            new Dictionary<string, string>());

        // Act
        await archiver.ArchiveAsync(ctx, CancellationToken.None);

        // Assert
        var manifestUpload = sink.Uploads.Single(u => u.RelativePath == "manifest.json");
        using var doc = System.Text.Json.JsonDocument.Parse(manifestUpload.Body);
        var root = doc.RootElement;

        root.GetProperty("swarmId").GetString().Should().Be(swarmId.ToString());
        root.GetProperty("goal").GetString().Should().Be("Audit the system.");
        root.GetProperty("templateKey").GetString().Should().Be("code-review");
        root.GetProperty("finalState").GetString().Should().Be(nameof(SwarmInstanceState.Failed));
        root.GetProperty("failureReason").GetString().Should().Be("boom");
        root.GetProperty("createdUtc").GetDateTime().Should().BeCloseTo(createdUtc, TimeSpan.FromSeconds(1));
        root.GetProperty("completedUtc").GetDateTime().Should().BeCloseTo(completedUtc, TimeSpan.FromSeconds(1));

        var agents = root.GetProperty("agents");
        agents.GetArrayLength().Should().Be(1, "the roster must be read from .chat/agents.json.");
        agents[0].GetProperty("name").GetString().Should().Be("security-reviewer-1");
        agents[0].GetProperty("role").GetString().Should().Be("security-reviewer");

        // With no run-context supplied, context is an empty object (not null).
        var context = root.GetProperty("context");
        context.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        context.EnumerateObject().Should().BeEmpty("no run-context was supplied for this run.");
    }

    /// <summary>
    /// Verifies the manifest carries the per-swarm run-context bag supplied at
    /// creation (the Feature-1 <c>ISwarmRunContext</c> values), so downstream
    /// consumers can correlate the archived run with its launch parameters.
    /// </summary>
    [TestMethod]
    public async Task ManifestCarriesSuppliedContext()
    {
        var chatDir = Path.Combine(this.workDir, ".chat");
        Directory.CreateDirectory(chatDir);
        await File.WriteAllTextAsync(Path.Combine(chatDir, "agents.json"), "[]");

        var sink = new RecordingBlobSink();
        var archiver = new BlobSwarmRunArchiver(sink, NullLoggerFactory.Instance);

        var ctx = new SwarmRunArchiveContext(
            Guid.NewGuid(),
            this.workDir,
            "goal",
            "code-review",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            SwarmInstanceState.Complete,
            null,
            new Dictionary<string, string> { ["sourceRoot"] = "/clones/pr-42", ["prId"] = "42" });

        await archiver.ArchiveAsync(ctx, CancellationToken.None);

        var manifestUpload = sink.Uploads.Single(u => u.RelativePath == "manifest.json");
        using var doc = System.Text.Json.JsonDocument.Parse(manifestUpload.Body);
        var context = doc.RootElement.GetProperty("context");
        context.GetProperty("sourceRoot").GetString().Should().Be("/clones/pr-42");
        context.GetProperty("prId").GetString().Should().Be("42");
    }

    private sealed class RecordingBlobSink : IArchiveBlobSink
    {
        private readonly ConcurrentQueue<RecordedUpload> uploads = new();

        public IReadOnlyList<RecordedUpload> Uploads => this.uploads.ToList();

        public async Task UploadAsync(string relativePath, Stream content, bool overwrite, CancellationToken cancellationToken)
        {
            // Drain the stream so the archiver's read path is exercised, then record.
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            this.uploads.Enqueue(new RecordedUpload(relativePath, overwrite, ms.ToArray()));
        }
    }

    private sealed record RecordedUpload(string RelativePath, bool Overwrite, byte[] Body)
    {
        public long ByteCount => this.Body.Length;
    }
}
