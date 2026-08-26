// ---------------------------------------------------------------------------
// WebHdfsClient.cs
// HDFS over HTTP, authenticated as the identity this service already is.
//
// Three things about this file are decisions rather than plumbing.
//
// The credential is the process's. UseDefaultCredentials puts the Windows
// logon session behind an HTTP Negotiate exchange, which for a service running
// as a gMSA means a Kerberos ticket the operating system obtained and rotated
// and that nothing here ever holds. There is no keytab to read, no password to
// resolve from a vault, and therefore nothing for this class to leak.
//
// Redirects are followed by the handler, not by hand. A WebHDFS OPEN answers
// 307 to a DataNode, and the temptation is to set an Authorization header and
// re-issue the GET - which fails, because .NET strips that header across hosts,
// and because the DataNode expects the block token that is already in the
// redirect URL rather than a second Negotiate exchange. Letting the handler
// follow the redirect with the same credentials is both simpler and correct.
// HttpFS avoids the hop entirely, which is why the configuration prefers it.
//
// A 401 or 403 is not an ingestion failure. It is this identity being refused,
// which is exit 3 in the contract, so it leaves here as
// PushSourceAuthenticationException rather than as an HTTP status the host
// would have to interpret.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hdfs;

using System.Net;
using System.Text.Json;
using PushCore;
using Serilog;

/// <summary>The WebHDFS and HttpFS operations this connector needs.</summary>
public sealed class WebHdfsClient : IDisposable
{
    private const int MaxAttempts = 4;

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly ILogger log;
    private readonly bool ownsClient;

    /// <summary>Initializes a new instance of the <see cref="WebHdfsClient"/> class.</summary>
    /// <param name="baseUrl">The base URL, ending in /webhdfs/v1.</param>
    /// <param name="log">Where to report progress.</param>
    public WebHdfsClient(string baseUrl, ILogger log)
        : this(baseUrl, CreateNegotiatingClient(), log, ownsClient: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebHdfsClient"/> class with a supplied client.</summary>
    /// <param name="baseUrl">The base URL, ending in /webhdfs/v1.</param>
    /// <param name="http">The client to use. A test supplies one over a fake handler.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="ownsClient">True when disposing this should dispose the client.</param>
    public WebHdfsClient(string baseUrl, HttpClient http, ILogger log, bool ownsClient = false)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.http = http;
        this.log = log;
        this.ownsClient = ownsClient;
    }

    /// <summary>Lists one directory. Does not recurse.</summary>
    /// <param name="path">Absolute HDFS path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its entries, each with an absolute path filled in.</returns>
    public async Task<IReadOnlyList<HdfsFileStatus>> ListAsync(string path, CancellationToken cancellationToken)
    {
        using JsonDocument document = await this.GetJsonAsync(path, "LISTSTATUS", cancellationToken);

        var entries = new List<HdfsFileStatus>();

        if (!document.RootElement.TryGetProperty("FileStatuses", out JsonElement statuses) ||
            !statuses.TryGetProperty("FileStatus", out JsonElement array))
        {
            return entries;
        }

        foreach (JsonElement element in array.EnumerateArray())
        {
            HdfsFileStatus status = ReadFileStatus(element);

            string suffix = element.TryGetProperty("pathSuffix", out JsonElement name)
                ? name.GetString() ?? string.Empty
                : string.Empty;

            status.Path = Join(path, suffix);
            entries.Add(status);
        }

        return entries;
    }

    /// <summary>Reads one path's status.</summary>
    /// <param name="path">Absolute HDFS path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status, or null when the path is gone.</returns>
    public async Task<HdfsFileStatus?> StatusAsync(string path, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await this.TryGetJsonAsync(path, "GETFILESTATUS", cancellationToken);

        if (document is null || !document.RootElement.TryGetProperty("FileStatus", out JsonElement element))
        {
            return null;
        }

        HdfsFileStatus status = ReadFileStatus(element);
        status.Path = path;
        return status;
    }

    /// <summary>Reads one path's extended ACL.</summary>
    /// <param name="path">Absolute HDFS path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ACL, or null when the path has none or is gone.</returns>
    public async Task<HdfsAclStatus?> AclAsync(string path, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await this.TryGetJsonAsync(path, "GETACLSTATUS", cancellationToken);

        if (document is null || !document.RootElement.TryGetProperty("AclStatus", out JsonElement element))
        {
            return null;
        }

        var acl = new HdfsAclStatus
        {
            Owner = Text(element, "owner"),
            Group = Text(element, "group"),
            Permission = Text(element, "permission"),
        };

        if (element.TryGetProperty("entries", out JsonElement entries))
        {
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string? value = entry.GetString();

                if (!string.IsNullOrEmpty(value))
                {
                    acl.Entries.Add(value);
                }
            }
        }

        return acl;
    }

    /// <summary>Counts the files under a path, for the budget preflight.</summary>
    /// <param name="path">Absolute HDFS path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The file count, or null when the summary is unavailable.</returns>
    public async Task<long?> FileCountAsync(string path, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await this.TryGetJsonAsync(path, "GETCONTENTSUMMARY", cancellationToken);

        if (document is null || !document.RootElement.TryGetProperty("ContentSummary", out JsonElement summary))
        {
            return null;
        }

        return summary.TryGetProperty("fileCount", out JsonElement count) ? count.GetInt64() : null;
    }

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">Absolute HDFS path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The content stream, which the caller disposes.</returns>
    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await this.SendAsync(path, "OPEN", cancellationToken);

        // Not disposed here: the stream belongs to the caller, and disposing the
        // response would close it underneath them.
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.ownsClient)
        {
            this.http.Dispose();
        }
    }

    /// <summary>
    /// Builds the client that authenticates as this process.
    ///
    /// PreAuthenticate keeps the Negotiate exchange from repeating on every
    /// request to a host already authenticated to, which at a million files is
    /// the difference between one round trip per file and three.
    /// </summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateNegotiatingClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            PreAuthenticate = true,
            AllowAutoRedirect = true,

            // A WebHDFS OPEN is NameNode then DataNode; anything beyond that is
            // a loop or a proxy nobody configured.
            MaxAutomaticRedirections = 4,
        };

        return new HttpClient(handler)
        {
            // Generous, because a DataNode streaming a large file legitimately
            // takes a while, and bounded, because a hung read must not stop a
            // scheduled crawl for ever.
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    private static HdfsFileStatus ReadFileStatus(JsonElement element)
    {
        long modified = element.TryGetProperty("modificationTime", out JsonElement time) ? time.GetInt64() : 0;

        return new HdfsFileStatus
        {
            Type = Text(element, "type"),
            Length = element.TryGetProperty("length", out JsonElement length) ? length.GetInt64() : 0,
            ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(modified),
            Owner = Text(element, "owner"),
            Group = Text(element, "group"),
            Permission = Text(element, "permission"),
        };
    }

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static string Join(string directory, string name)
    {
        if (name.Length == 0)
        {
            return directory;
        }

        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string op, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await this.SendAsync(path, op, cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string path, string op, CancellationToken cancellationToken)
    {
        try
        {
            return await this.GetJsonAsync(path, op, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // A file deleted between the listing and the read. Normal in a live
            // lake; the caller skips it.
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string op, CancellationToken cancellationToken)
    {
        string url = $"{this.baseUrl}{EncodePath(path)}?op={op}";

        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;

            try
            {
                response = await this.http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                // The socket, not the cluster's answer. A NameNode failing over
                // and a DataNode restarting both look like this.
                await this.BackoffAsync(op, path, ex.Message, attempt, cancellationToken);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                await this.BackoffAsync(op, path, "the request timed out", attempt, cancellationToken);
                GC.KeepAlive(ex);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Exit 3, not exit 4. An expired ticket, a principal removed
                    // from a Ranger policy and a cluster that stopped trusting
                    // this realm all land here, and every one of them is a
                    // credential problem rather than a bug in the data path.
                    throw new PushSourceAuthenticationException(
                        $"HDFS refused this identity with {(int)response.StatusCode} for {op} on {path}. " +
                        "Check that the service account still holds a Kerberos ticket for the cluster's realm " +
                        "and that Ranger still grants it read on this path.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new HttpRequestException(
                        $"{path} does not exist.", inner: null, statusCode: HttpStatusCode.NotFound);
                }

                bool transient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;

                if (transient && attempt < MaxAttempts)
                {
                    await this.BackoffAsync(op, path, $"status {(int)response.StatusCode}", attempt, cancellationToken);
                    continue;
                }

                // No body in the message: a WebHDFS error body echoes the path
                // and a Java stack trace, and neither belongs in a log that a
                // wider group can read than can read the cluster.
                throw new HttpRequestException(
                    $"{op} on {path} returned {(int)response.StatusCode}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }
        }
    }

    private async Task BackoffAsync(
        string op, string path, string reason, int attempt, CancellationToken cancellationToken)
    {
        TimeSpan wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));

        this.log.Warning(
            "{Operation} on {Path} failed ({Reason}). Waiting {Seconds}s before attempt {Next} of {Max}.",
            op,
            path,
            reason,
            (int)wait.TotalSeconds,
            attempt + 1,
            MaxAttempts);

        await Task.Delay(wait, cancellationToken);
    }

    /// <summary>
    /// Percent-encodes a path for the URL without encoding its separators.
    ///
    /// A lake is full of names with spaces, ampersands and non-ASCII letters in
    /// them, and each of those breaks a URL differently.
    /// </summary>
    /// <param name="path">The absolute HDFS path.</param>
    /// <returns>The encoded path.</returns>
    public static string EncodePath(string path)
    {
        return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    }
}
