// ---------------------------------------------------------------------------
// CdpCrawlResilienceTests.cs
// The three ways the HDFS crawl used to lose files, each pinned by a test that
// fails without its fix.
//
// They are grouped here rather than beside the ordering tests in CdpSourceTests
// because they are all the same kind of fault: not a wrong answer, but a
// SILENT one. Every one of them left a crawl reporting success.
//
//   * A full recrawl reads oldest-first, so one truncated by
//     Settings:MaxItemsPerRun ends on an EARLY file. Writing that position over
//     a later high-water mark, and counting the run as a completed crawl,
//     abandoned everything between the two for ever - the newest files in the
//     lake were never indexed by any run. The marker is therefore monotonic and
//     a truncated run does not count towards the recrawl cadence.
//
//   * One file deleted by a retention job between the listing and the read
//     ended a crawl of a million. A deleted file is now skipped, and - the
//     distinction that matters - a file the cluster merely will not hand over
//     is still an extraction failure that spends the error budget.
//
//   * HDFS paths are case-sensitive, so /data/root/HR and /data/root/hr are two
//     directories. A case-insensitive visited set dropped the second one and
//     its entire subtree with no warning and no failure.
//
// The last of those needs a cluster that compares paths the way HDFS does,
// which TestSupport's FakeWebHdfs cannot be: it keys its tree
// case-insensitively, so it cannot hold both directories at once. Hence the
// small ordinal cluster at the foot of this file, used by that test alone.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Connector.Extraction;
    using CdpConnector.Source;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using CdpConnector.Source.Ranger;
    using CdpConnector.Source.Watermark;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpCrawlResilienceTests : IDisposable
    {
        private static readonly DateTimeOffset Base = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

        private readonly string stateDirectory =
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(this.stateDirectory))
            {
                Directory.Delete(this.stateDirectory, true);
            }
        }

        // ------------------------------------------------------------------
        // The watermark, and what a truncated recrawl may claim about it
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_capped_full_recrawl_does_not_move_the_marker_backwards()
        {
            // The whole defect in one run. The recrawl ignores the marker, so it
            // starts at the OLDEST file; the cap stops it two files in; and the
            // break ends the iterator normally, so the engine calls
            // OnCrawlCompletedAsync exactly as it would after a real crawl.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            var store = new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None);

            string late = Base.AddMinutes(2).UtcDateTime.ToString("o");

            store.Write(new CrawlCheckpoint
            {
                MarkerTime = late,
                MarkerKey = "/data/contracts/c.txt",

                // Run 7 done, so the next run is a multiple of the cadence and
                // therefore a full recrawl.
                RunCount = 7,
            });

            List<PushItem> items = await this.RunAsync(
                cluster, cluster.BaseUrl, "/data/contracts", fullRecrawlEveryRuns: 7, maxItemsPerRun: 2);

            Assert.Equal(
                new[] { "/data/contracts/a.txt", "/data/contracts/b.txt" },
                items.Select(item => (string)item.Properties["itemPath"]).ToArray());

            CrawlCheckpoint after = store.Read();

            // The high-water mark is still the high-water mark. Without the
            // comparison in FlushMarker this would now read b.txt, and c.txt -
            // with every file after it - would never be read by any run again.
            Assert.Equal(late, after.MarkerTime);
            Assert.Equal("/data/contracts/c.txt", after.MarkerKey);

            // And the recrawl is still owed. Counting this run would abandon it
            // for another seven runs on the strength of two files.
            Assert.Equal(7, after.RunCount);
        }

        [Fact]
        public async Task The_uncapped_full_recrawl_still_advances_the_run_count_and_writes_the_newest_marker()
        {
            // Deliberately a guard rather than a reproduction: it passes before
            // the fix as well as after it. Refusing a marker and refusing a run
            // count are both ways of standing still, and a fix that stood still
            // when the recrawl HAD covered the corpus would disable the ACL
            // staleness bound instead of protecting it.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            var store = new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None);

            store.Write(new CrawlCheckpoint
            {
                MarkerTime = Base.UtcDateTime.ToString("o"),
                MarkerKey = "/data/contracts/a.txt",
                RunCount = 7,
            });

            List<PushItem> items = await this.RunAsync(
                cluster, cluster.BaseUrl, "/data/contracts", fullRecrawlEveryRuns: 7, maxItemsPerRun: 0);

            Assert.Equal(
                new[] { "/data/contracts/a.txt", "/data/contracts/b.txt", "/data/contracts/c.txt" },
                items.Select(item => (string)item.Properties["itemPath"]).ToArray());

            CrawlCheckpoint after = store.Read();

            Assert.Equal("/data/contracts/c.txt", after.MarkerKey);
            Assert.Equal(8, after.RunCount);
        }

        // ------------------------------------------------------------------
        // A file that goes away, and a file the cluster will not hand over
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_file_deleted_between_the_listing_and_the_read_is_skipped_rather_than_fatal()
        {
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            // One retention job, one file, mid-crawl. Only the OPEN 404s: the
            // listing already happened, which is exactly the race.
            cluster.FailWith = (op, path) =>
                op == "OPEN" && path == "/data/contracts/b.txt"
                    ? (HttpStatusCode?)HttpStatusCode.NotFound
                    : null;

            var items = new List<PushItem>();

            await using (HdfsPushSource source = this.Source(
                cluster, cluster.BaseUrl, "/data/contracts", fullRecrawlEveryRuns: 0, maxItemsPerRun: 0))
            {
                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    items.Add(item);
                }

                // Skipped, not failed. There is no file left to have failed, so
                // it must not spend the error budget that exists to catch a sick
                // DataNode.
                Assert.Equal(1, source.Skipped);
            }

            Assert.Equal(
                new[] { "/data/contracts/a.txt", "/data/contracts/c.txt" },
                items.Select(item => (string)item.Properties["itemPath"]).ToArray());
        }

        [Fact]
        public async Task A_file_the_cluster_will_not_hand_over_is_indexed_without_its_body()
        {
            // The other side of the same fix, and the reason the 404 is singled
            // out rather than every failed open being treated as a deletion. A
            // 500 is a file that still exists, so it is indexed by its metadata
            // and counted against the error budget.
            //
            // This one takes about fourteen seconds: WebHdfsClient retries a 5xx
            // three times with a doubling backoff, and what is being asserted is
            // that the failure is still contained after that ladder is spent.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            cluster.FailWith = (op, path) =>
                op == "OPEN" && path == "/data/contracts/b.txt"
                    ? (HttpStatusCode?)HttpStatusCode.InternalServerError
                    : null;

            List<PushItem> items = await this.CrawlAsync(cluster, cluster.BaseUrl, "/data/contracts");

            Assert.Equal(
                new[] { "/data/contracts/a.txt", "/data/contracts/b.txt", "/data/contracts/c.txt" },
                items.Select(item => (string)item.Properties["itemPath"]).ToArray());

            PushItem unread = items.Single(
                item => (string)item.Properties["itemPath"] == "/data/contracts/b.txt");

            Assert.Equal("Failed", unread.Properties["extractStatus"]);
            Assert.Equal(string.Empty, unread.Content);

            // Metadata-only is not a reason to relax who may see it.
            Assert.NotEmpty(unread.Acl);
        }

        // ------------------------------------------------------------------
        // Paths as HDFS spells them
        // ------------------------------------------------------------------

        [Fact]
        public async Task Two_directories_differing_only_in_case_are_both_crawled()
        {
            // On a case-insensitive visited set the walk enters HR, marks it
            // seen, and then silently declines to enter hr - dropping beta.txt
            // and everything else under it with no warning and no failure.
            var cluster = new CaseSensitiveHdfs()
                .File("/data/root/HR/alpha.txt", "upper")
                .File("/data/root/hr/beta.txt", "lower");

            List<PushItem> items = await this.CrawlAsync(cluster, cluster.BaseUrl, "/data/root");

            Assert.Equal(
                new[] { "/data/root/HR/alpha.txt", "/data/root/hr/beta.txt" },
                items.Select(item => (string)item.Properties["itemPath"])
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray());
        }

        [Fact]
        public async Task A_root_written_with_a_trailing_slash_is_the_same_root()
        {
            // The cost of the visited set being ordinal, paid once in
            // CdpSettings rather than at every comparison. Before the
            // normalisation, "/data/contracts/" reached the NameNode verbatim,
            // 404'd, and the whole root was skipped with a warning that reads
            // like a configuration mistake at the cluster.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1));

            string[] plain = (await this.CrawlAsync(cluster, cluster.BaseUrl, "/data/contracts"))
                .Select(item => (string)item.Properties["itemPath"]).ToArray();

            string[] slashed = (await this.CrawlAsync(cluster, cluster.BaseUrl, "/data/contracts/"))
                .Select(item => (string)item.Properties["itemPath"]).ToArray();

            Assert.Equal(new[] { "/data/contracts/a.txt", "/data/contracts/b.txt" }, plain);
            Assert.Equal(plain, slashed);

            // The doubled separator is the same problem written the other way,
            // and the crawl's own derived paths never contain one.
            PushOptions options = CdpConnectorTests.CdpOptions();
            options.Settings["HdfsRoots"] = "/data//contracts/;/";

            Assert.Equal(
                new[] { "/data/contracts", "/" },
                CdpSettings.From(options).HdfsRoots.ToArray());
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Reads a crawl to its end without committing anything.</summary>
        private async Task<List<PushItem>> CrawlAsync(HttpMessageHandler cluster, string baseUrl, string roots)
        {
            var items = new List<PushItem>();

            await using HdfsPushSource source = this.Source(
                cluster, baseUrl, roots, fullRecrawlEveryRuns: 0, maxItemsPerRun: 0);

            await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
            {
                items.Add(item);
            }

            return items;
        }

        /// <summary>Reads a crawl, confirms every item, and completes it.</summary>
        private async Task<List<PushItem>> RunAsync(
            HttpMessageHandler cluster,
            string baseUrl,
            string roots,
            int fullRecrawlEveryRuns,
            int maxItemsPerRun)
        {
            var items = new List<PushItem>();

            await using (HdfsPushSource source = this.Source(
                cluster, baseUrl, roots, fullRecrawlEveryRuns, maxItemsPerRun))
            {
                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    items.Add(item);
                }

                foreach (PushItem item in items)
                {
                    await source.OnItemCommittedAsync(item, CancellationToken.None);
                }

                await source.OnCrawlCompletedAsync(CancellationToken.None);
            }

            return items;
        }

        private HdfsPushSource Source(
            HttpMessageHandler cluster,
            string baseUrl,
            string roots,
            int fullRecrawlEveryRuns,
            int maxItemsPerRun)
        {
            PushOptions options = CdpConnectorTests.CdpOptions();
            options.Settings["HdfsBaseUrl"] = baseUrl;
            options.Settings["HdfsRoots"] = roots;
            options.Settings["IncludeExtensions"] = "txt;md;html;docx";
            options.Settings["FullRecrawlEveryRuns"] = fullRecrawlEveryRuns.ToString();
            options.Settings["MaxItemsPerRun"] = maxItemsPerRun.ToString();
            options.Settings["ScanSlackSeconds"] = "0";
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

            CdpSettings settings = CdpSettings.From(options);

            var principals = new PrincipalResolver(
                new Dictionary<string, string> { ["hadoop-contracts-read"] = TestData.GroupObjectId },
                graph: null,
                Logger.None);

            return new HdfsPushSource(
                settings,

                // Not the owner of the handler: a test that crawls the same
                // cluster twice would otherwise be handing the second run a
                // client whose handler the first one disposed.
                new WebHdfsClient(baseUrl, new HttpClient(cluster), Logger.None, ownsClient: false),
                new RangerPolicyClient(
                    "https://ranger.test:6182",
                    new HttpClient(new EmptyRanger()),
                    Logger.None,
                    ownsClient: true),
                new HdfsAclBuilder(principals, string.Empty),
                TextExtractorSet.Default(),
                new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None),
                Logger.None);
        }

        /// <summary>A Ranger with no policies, so the files' own permissions decide.</summary>
        private sealed class EmptyRanger : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }
        }

        /// <summary>
        /// A cluster that tells /data/root/HR and /data/root/hr apart.
        ///
        /// TestSupport's FakeWebHdfs keys its tree with OrdinalIgnoreCase and
        /// matches its listing prefixes the same way, so it cannot hold two
        /// paths differing only in case - which is the one arrangement this
        /// test needs. Everything here is ordinal, and it serves only the three
        /// operations a crawl issues.
        /// </summary>
        private sealed class CaseSensitiveHdfs : HttpMessageHandler
        {
            private readonly Dictionary<string, string> files =
                new Dictionary<string, string>(StringComparer.Ordinal);

            private readonly HashSet<string> directories = new HashSet<string>(StringComparer.Ordinal);

            public string BaseUrl => "https://httpfs.test:14000/webhdfs/v1";

            public CaseSensitiveHdfs File(string path, string content)
            {
                this.files[path] = content;

                string parent = path.Substring(0, path.LastIndexOf('/'));

                while (parent.Length > 0)
                {
                    this.directories.Add(parent);
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                }

                return this;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string query = request.RequestUri.Query;
                string op = query.Substring(query.IndexOf("op=", StringComparison.Ordinal) + 3);

                int ampersand = op.IndexOf('&');
                if (ampersand >= 0)
                {
                    op = op.Substring(0, ampersand);
                }

                string path = Uri.UnescapeDataString(
                    request.RequestUri.AbsolutePath.Replace("/webhdfs/v1", string.Empty, StringComparison.Ordinal));

                if (op == "LISTSTATUS" && this.directories.Contains(path))
                {
                    return Json("{\"FileStatuses\":{\"FileStatus\":[" + this.Children(path) + "]}}");
                }

                if (this.files.TryGetValue(path, out string content))
                {
                    if (op == "OPEN")
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(content, Encoding.UTF8),
                        });
                    }

                    if (op == "GETACLSTATUS")
                    {
                        return Json(
                            "{\"AclStatus\":{\"owner\":\"svc_ingest\",\"group\":\"hadoop-contracts-read\"," +
                            "\"permission\":\"640\",\"entries\":[]}}");
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static Task<HttpResponseMessage> Json(string body)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            private string Children(string directory)
            {
                string prefix = directory + "/";

                IEnumerable<string> children = this.directories
                    .Concat(this.files.Keys)
                    .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(path => !path.Substring(prefix.Length).Contains('/'))
                    .OrderBy(path => path, StringComparer.Ordinal);

                return string.Join(",", children.Select(this.FileStatus));
            }

            private string FileStatus(string path)
            {
                bool isDirectory = this.directories.Contains(path);
                string content = isDirectory ? string.Empty : this.files[path];

                return "{" +
                    "\"pathSuffix\":\"" + path.Substring(path.LastIndexOf('/') + 1) + "\"," +
                    "\"type\":\"" + (isDirectory ? "DIRECTORY" : "FILE") + "\"," +
                    "\"length\":" + Encoding.UTF8.GetByteCount(content) + "," +
                    "\"modificationTime\":" + Base.ToUnixTimeMilliseconds() + "," +
                    "\"owner\":\"svc_ingest\"," +
                    "\"group\":\"hadoop-contracts-read\"," +
                    "\"permission\":\"" + (isDirectory ? "755" : "640") + "\"}";
            }
        }
    }
}
