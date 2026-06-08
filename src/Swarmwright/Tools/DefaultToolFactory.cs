using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tools;

/// <summary>
/// Creates the safe default tool set available to swarm worker agents:
/// <c>read</c>, <c>write</c>, and <c>web_fetch</c>. All file tools are scoped to a per-swarm
/// work directory; <c>web_fetch</c> blocks loopback and private IP ranges.
/// </summary>
public static partial class DefaultToolFactory
{
    private const int MaxReadBytes = 50 * 1024;
    private const int MaxWriteBytes = 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex("<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Creates the default tool set scoped to the given work directory.
    /// </summary>
    /// <param name="workDirectory">The per-swarm work directory the file tools are confined to.</param>
    /// <param name="httpClient">The HTTP client used by <c>web_fetch</c>.</param>
    /// <returns>The list of default <see cref="AITool"/> instances.</returns>
    public static IList<AITool> CreateDefaultTools(string workDirectory, HttpClient httpClient)
    {
        return
        [
            CreateReadTool(workDirectory),
            CreateWriteTool(workDirectory),
            CreateWebFetchTool(httpClient),
        ];
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateReadTool(string workDirectory)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Path of the file to read, relative to your work directory.")] string path) =>
            {
                try
                {
                    if (!PathSecurity.TryResolveSafePath(workDirectory, path, out var resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = "Path is outside work directory or invalid." },
                            JsonOptions);
                    }

                    if (!File.Exists(resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"File not found: {path}" },
                            JsonOptions);
                    }

                    var bytes = await File.ReadAllBytesAsync(resolved).ConfigureAwait(false);
                    var truncated = bytes.Length > MaxReadBytes;
                    var contentBytes = truncated ? bytes[..MaxReadBytes] : bytes;
                    var content = System.Text.Encoding.UTF8.GetString(contentBytes);

                    return JsonSerializer.Serialize(
                        new { content, bytes = bytes.Length, truncated },
                        JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "read",
            "Reads a file from your work directory. Path must be relative to the work directory.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateWriteTool(string workDirectory)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Path of the file to write, relative to your work directory.")] string path,
                [Description("Full content to write to the file.")] string content) =>
            {
                try
                {
                    if (content is { Length: > MaxWriteBytes })
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"Content too large (max {MaxWriteBytes} bytes)." },
                            JsonOptions);
                    }

                    if (!PathSecurity.TryResolveSafePath(workDirectory, path, out var resolved))
                    {
                        return JsonSerializer.Serialize(
                            new { error = "Path is outside work directory or invalid." },
                            JsonOptions);
                    }

                    var parentDir = Path.GetDirectoryName(resolved);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    await File.WriteAllTextAsync(resolved, content ?? string.Empty).ConfigureAwait(false);

                    return JsonSerializer.Serialize(
                        new { success = true, path, bytes = (content ?? string.Empty).Length },
                        JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "write",
            "Writes a file to your work directory. Creates parent directories if needed. Path must be relative.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tool functions must return error JSON to the LLM, not throw.")]
    private static AIFunction CreateWebFetchTool(HttpClient httpClient)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("HTTP or HTTPS URL to fetch.")] string url,
                [Description("Optional description of what you want to extract from the page.")] string prompt) =>
            {
                _ = prompt; // currently informational only
                try
                {
                    if (!IsAllowedUrl(url, out var blockReason))
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"URL blocked: {blockReason}" },
                            JsonOptions);
                    }

                    using var cts = new CancellationTokenSource(FetchTimeout);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", "Swarmwright-Swarm-Agent/1.0");

                    using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        return JsonSerializer.Serialize(
                            new { error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" },
                            JsonOptions);
                    }

                    var rawBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    var stripped = StripHtml(rawBody);
                    var truncated = stripped.Length > MaxReadBytes;
                    var content = truncated ? stripped[..MaxReadBytes] : stripped;

                    return JsonSerializer.Serialize(
                        new { content, url, truncated },
                        JsonOptions);
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                }
            },
            "web_fetch",
            "Fetches a public HTTP/HTTPS URL and returns its text content. Loopback and private IPs are blocked.");
    }

    private static bool IsAllowedUrl(string url, out string blockReason)
    {
        blockReason = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            blockReason = "URL is empty";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            blockReason = "URL is malformed";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            blockReason = $"only http/https schemes are allowed (got '{uri.Scheme}')";
            return false;
        }

        var host = uri.Host;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            blockReason = "loopback hostnames are not allowed";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                blockReason = "loopback IPs are not allowed";
                return false;
            }

            if (IsPrivateAddress(ip))
            {
                blockReason = "private IP ranges are not allowed";
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();

        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return true;
        }

        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        // 169.254.0.0/16 (link-local)
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        return false;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        // Drop script/style blocks first, then strip remaining tags, collapse whitespace.
        var noScripts = ScriptBlockRegex().Replace(html, " ");
        var noStyles = StyleBlockRegex().Replace(noScripts, " ");
        var noTags = HtmlTagRegex().Replace(noStyles, " ");
        var collapsed = WhitespaceRegex().Replace(noTags, " ").Trim();
        return WebUtility.HtmlDecode(collapsed);
    }
}
