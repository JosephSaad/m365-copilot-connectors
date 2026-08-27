// ---------------------------------------------------------------------------
// FakeWebHdfs.cs
// A WebHDFS-shaped cluster in memory, so the crawl can be tested without one.
//
// It answers the four operations the connector uses - LISTSTATUS,
// GETFILESTATUS, GETACLSTATUS and OPEN - from a tree the test builds, and it
// can be told to fail: a 401 to prove that a refused identity becomes exit 3,
// a 503 to prove the retry, a 404 mid-crawl to prove that a file deleted
// between the listing and the read is skipped rather than fatal.
//
// The JSON shapes are Apache's, not this connector's, on purpose. A fake that
// returned what the parser wanted would prove only that the parser agrees with
// itself.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>One file or directory in the fake cluster.</summary>
    public sealed class FakeHdfsEntry
    {
        public string Path { get; set; }

        public bool IsDirectory { get; set; }

        public string Content { get; set; } = string.Empty;

        public DateTimeOffset Modified { get; set; } = DateTimeOffset.UnixEpoch;

        public string Owner { get; set; } = "svc_ingest";

        public string Group { get; set; } = "hadoop-contracts-read";

        /// <summary>POSIX triple. 640 grants the owning group read; 600 does not.</summary>
        public string Permission { get; set; } = "640";

        /// <summary>Extended ACL entries, for example "group:analysts:r--".</summary>
        public List<string> AclEntries { get; } = new List<string>();
    }

    /// <summary>Serves a canned HDFS tree over HTTP.</summary>
    public sealed class FakeWebHdfs : HttpMessageHandler
    {
        private readonly Dictionary<string, FakeHdfsEntry> entries =
            new Dictionary<string, FakeHdfsEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the operations requested, as "OP path", in order.</summary>
        public List<string> Requests { get; } = new List<string>();

        /// <summary>Gets or sets a hook returning a status to answer with instead, or null to serve normally.</summary>
        public Func<string, string, HttpStatusCode?> FailWith { get; set; }

        /// <summary>Gets or sets the base URL this fake is mounted at.</summary>
        public string BaseUrl { get; set; } = "https://httpfs.test:14000/webhdfs/v1";

        /// <summary>Adds a directory. 755 is world-traversable; 750 is not.</summary>
        public FakeWebHdfs Directory(
            string path,
            string permission = "755",
            string group = "hadoop-contracts-read",
            params string[] aclEntries)
        {
            var entry = new FakeHdfsEntry
            {
                Path = path,
                IsDirectory = true,
                Permission = permission,
                Group = group,
            };

            entry.AclEntries.AddRange(aclEntries);
            this.entries[path] = entry;
            return this;
        }

        /// <summary>Adds a file.</summary>
        public FakeWebHdfs File(
            string path,
            string content,
            DateTimeOffset modified,
            string permission = "640",
            string group = "hadoop-contracts-read",
            params string[] aclEntries)
        {
            var entry = new FakeHdfsEntry
            {
                Path = path,
                IsDirectory = false,
                Content = content,
                Modified = modified,
                Permission = permission,
                Group = group,
            };

            entry.AclEntries.AddRange(aclEntries);
            this.entries[path] = entry;

            // Every parent must exist for a listing to reach the file.
            string parent = path.Substring(0, path.LastIndexOf('/'));

            while (parent.Length > 0 && !this.entries.ContainsKey(parent))
            {
                this.Directory(parent);
                parent = parent.Substring(0, parent.LastIndexOf('/'));
            }

            // A parent the test configured deliberately is left exactly as it
            // set it - the loop above only fills in the ones nobody named.

            return this;
        }

        /// <summary>Builds a client bound to this fake.</summary>
        public HttpClient Client()
        {
            return new HttpClient(this);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri.ToString();
            string query = request.RequestUri.Query;
            string op = query.Contains("op=") ? query.Substring(query.IndexOf("op=", StringComparison.Ordinal) + 3) : string.Empty;

            int ampersand = op.IndexOf('&');
            if (ampersand >= 0)
            {
                op = op.Substring(0, ampersand);
            }

            string path = Uri.UnescapeDataString(
                request.RequestUri.AbsolutePath.Replace("/webhdfs/v1", string.Empty, StringComparison.Ordinal));

            if (path.Length == 0)
            {
                path = "/";
            }

            this.Requests.Add(op + " " + path);

            HttpStatusCode? forced = this.FailWith?.Invoke(op, path);

            if (forced.HasValue)
            {
                return Task.FromResult(new HttpResponseMessage(forced.Value));
            }

            if (!this.entries.TryGetValue(path, out FakeHdfsEntry entry))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            string body;

            switch (op)
            {
                case "LISTSTATUS":
                    body = this.ListStatus(path);
                    break;

                case "GETFILESTATUS":
                    body = "{\"FileStatus\":" + FileStatus(entry, includeName: false) + "}";
                    break;

                case "GETACLSTATUS":
                    body = AclStatus(entry);
                    break;

                case "GETCONTENTSUMMARY":
                    body = "{\"ContentSummary\":{\"fileCount\":" +
                           this.entries.Values.Count(e => !e.IsDirectory) + "}}";
                    break;

                case "OPEN":
                    var file = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(entry.Content, Encoding.UTF8),
                    };
                    return Task.FromResult(file);

                default:
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private string ListStatus(string directory)
        {
            string prefix = directory.EndsWith("/", StringComparison.Ordinal) ? directory : directory + "/";

            IEnumerable<FakeHdfsEntry> children = this.entries.Values
                .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Path.Substring(prefix.Length).Contains('/'))
                .OrderBy(e => e.Path, StringComparer.Ordinal);

            var parts = children.Select(e => FileStatus(e, includeName: true));

            return "{\"FileStatuses\":{\"FileStatus\":[" + string.Join(",", parts) + "]}}";
        }

        private static string FileStatus(FakeHdfsEntry entry, bool includeName)
        {
            string name = entry.Path.Substring(entry.Path.LastIndexOf('/') + 1);

            return "{" +
                (includeName ? "\"pathSuffix\":\"" + name + "\"," : "\"pathSuffix\":\"\",") +
                "\"type\":\"" + (entry.IsDirectory ? "DIRECTORY" : "FILE") + "\"," +
                "\"length\":" + Encoding.UTF8.GetByteCount(entry.Content) + "," +
                "\"modificationTime\":" + entry.Modified.ToUnixTimeMilliseconds() + "," +
                "\"owner\":\"" + entry.Owner + "\"," +
                "\"group\":\"" + entry.Group + "\"," +
                "\"permission\":\"" + entry.Permission + "\"}";
        }

        private static string AclStatus(FakeHdfsEntry entry)
        {
            string entries = string.Join(",", entry.AclEntries.Select(e => "\"" + e + "\""));

            return "{\"AclStatus\":{" +
                "\"owner\":\"" + entry.Owner + "\"," +
                "\"group\":\"" + entry.Group + "\"," +
                "\"permission\":\"" + entry.Permission + "\"," +
                "\"entries\":[" + entries + "]}}";
        }
    }
}
