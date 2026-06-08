using System.Net;
using System.Text.Json;
using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="DefaultToolFactory"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DefaultToolFactoryTests : IDisposable
{
    private string workDir = null!;
    private HttpClient httpClient = null!;
    private FakeHttpMessageHandler fakeHandler = null!;

    /// <summary>
    /// Disposes any leaked instances. Per-test cleanup happens in <see cref="TestCleanup"/>.
    /// </summary>
    public void Dispose()
    {
        this.httpClient?.Dispose();
        this.fakeHandler?.Dispose();
    }

    /// <summary>
    /// Creates a unique temp work directory before each test.
    /// </summary>
    [TestInitialize]
    public void TestInitialize()
    {
        this.workDir = Path.Combine(Path.GetTempPath(), "swarm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workDir);
        this.fakeHandler = new FakeHttpMessageHandler();
        this.httpClient = new HttpClient(this.fakeHandler);
    }

    /// <summary>
    /// Cleans up the work directory after each test.
    /// </summary>
    [TestCleanup]
    public void TestCleanup()
    {
        this.httpClient.Dispose();
        if (Directory.Exists(this.workDir))
        {
            Directory.Delete(this.workDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // CreateDefaultTools — composition
    // -----------------------------------------------------------------------

    [TestMethod]
    public void CreateDefaultTools_Returns_Three_Tools()
    {
        var tools = DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient);

        tools.Should().HaveCount(3);
        tools.Select(t => t.Name).Should().BeEquivalentTo(["read", "write", "web_fetch"]);
    }

    // -----------------------------------------------------------------------
    // read tool
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Read_HappyPath_ReturnsFileContent()
    {
        // Arrange
        var filePath = Path.Combine(this.workDir, "notes.txt");
        await File.WriteAllTextAsync(filePath, "hello world");
        var readTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "read");

        // Act
        var resultJson = await readTool.InvokeAsync(new AIFunctionArguments { ["path"] = "notes.txt" });

        // Assert
        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("content").GetString().Should().Be("hello world");
    }

    [TestMethod]
    public async Task Read_RejectsPathEscape_DotDot()
    {
        var readTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "read");

        var resultJson = await readTool.InvokeAsync(new AIFunctionArguments { ["path"] = "../../../etc/passwd" });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("outside work directory");
    }

    [TestMethod]
    public async Task Read_RejectsAbsolutePath()
    {
        var readTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "read");

        var absolutePath = OperatingSystem.IsWindows() ? @"C:\Windows\system.ini" : "/etc/passwd";
        var resultJson = await readTool.InvokeAsync(new AIFunctionArguments { ["path"] = absolutePath });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task Read_FileNotFound_ReturnsError()
    {
        var readTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "read");

        var resultJson = await readTool.InvokeAsync(new AIFunctionArguments { ["path"] = "nonexistent.txt" });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("not found");
    }

    [TestMethod]
    public async Task Read_OversizedFile_TruncatesAt50KB()
    {
        var filePath = Path.Combine(this.workDir, "big.txt");
        await File.WriteAllTextAsync(filePath, new string('x', 100_000));
        var readTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "read");

        var resultJson = await readTool.InvokeAsync(new AIFunctionArguments { ["path"] = "big.txt" });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        var content = doc.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        content!.Length.Should().BeLessThanOrEqualTo(51_200); // 50 KiB
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // write tool
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Write_HappyPath_CreatesFile()
    {
        var writeTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "write");

        var resultJson = await writeTool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "report.md",
            ["content"] = "# Report\n\nDone.",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var written = await File.ReadAllTextAsync(Path.Combine(this.workDir, "report.md"));
        written.Should().Be("# Report\n\nDone.");
    }

    [TestMethod]
    public async Task Write_CreatesParentDirectory()
    {
        var writeTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "write");

        var resultJson = await writeTool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "subdir/nested/file.txt",
            ["content"] = "data",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        File.Exists(Path.Combine(this.workDir, "subdir", "nested", "file.txt")).Should().BeTrue();
    }

    [TestMethod]
    public async Task Write_RejectsPathEscape()
    {
        var writeTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "write");

        var resultJson = await writeTool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "../escaped.txt",
            ["content"] = "bad",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("outside work directory");
        File.Exists(Path.Combine(Path.GetDirectoryName(this.workDir)!, "escaped.txt")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Write_RejectsOversizedContent()
    {
        var writeTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "write");

        var huge = new string('x', 2_000_000); // 2 MB > 1 MB limit
        var resultJson = await writeTool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "huge.txt",
            ["content"] = huge,
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("too large");
    }

    // -----------------------------------------------------------------------
    // web_fetch tool
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task WebFetch_HappyPath_ReturnsContent()
    {
        this.fakeHandler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Hello world</body></html>"),
        };
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "https://example.com/page",
            ["prompt"] = "Get the content",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("content").GetString().Should().Contain("Hello world");
    }

    [TestMethod]
    public async Task WebFetch_RejectsLocalhost()
    {
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "http://localhost:8080/admin",
            ["prompt"] = "fetch",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("blocked");
    }

    [TestMethod]
    public async Task WebFetch_RejectsLoopback127()
    {
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "http://127.0.0.1/secrets",
            ["prompt"] = "fetch",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("blocked");
    }

    [TestMethod]
    public async Task WebFetch_RejectsPrivateIp192()
    {
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "http://192.168.1.1/router",
            ["prompt"] = "fetch",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("blocked");
    }

    [TestMethod]
    public async Task WebFetch_RejectsFileScheme()
    {
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "file:///etc/passwd",
            ["prompt"] = "fetch",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("blocked");
    }

    [TestMethod]
    public async Task WebFetch_NonSuccessStatus_ReturnsError()
    {
        this.fakeHandler.Response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not Found"),
        };
        var fetchTool = (AIFunction)DefaultToolFactory.CreateDefaultTools(this.workDir, this.httpClient)
            .First(t => t.Name == "web_fetch");

        var resultJson = await fetchTool.InvokeAsync(new AIFunctionArguments
        {
            ["url"] = "https://example.com/missing",
            ["prompt"] = "fetch",
        });

        var doc = JsonDocument.Parse(resultJson?.ToString() ?? "{}");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("404");
    }

    /// <summary>
    /// Minimal HttpMessageHandler that returns a configurable response.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(this.Response);
        }
    }
}
