// ---------------------------------------------------------------------------
// CdpSourceTests.cs
// The HDFS crawl end to end, against a cluster made of canned JSON.
//
// What these are really testing is the order of operations, because that is
// where a file connector goes wrong quietly:
//
//   * files come out in (modification time, path) order, which is the only
//     thing that makes a checkpoint mean anything;
//   * a resumed crawl reads what is after the marker and nothing else;
//   * the periodic full recrawl ignores the marker, because it is the only
//     thing that re-derives an ACL after a permission change at the source;
//   * a file nobody can be granted is skipped BEFORE its content is read, so a
//     document that must not be indexed is never even fetched;
//   * a document whose text cannot be extracted is still indexed, by name and
//     path, with a property saying why - findable rather than absent.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Connector.Extraction;
    using CdpConnector.Source;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using CdpConnector.Source.Ranger;
    using CdpConnector.Source.Watermark;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpSourceTests : IDisposable
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

        [Fact]
        public async Task Files_come_out_oldest_first_with_the_path_breaking_ties()
        {
            // The ordering IS the checkpoint's meaning: a run interrupted at any
            // point has written a prefix of this sequence.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/c.txt", "third", Base.AddMinutes(2))
                .File("/data/contracts/a.txt", "first", Base)
                .File("/data/contracts/b.txt", "second", Base.AddMinutes(1))

                // Same timestamp as a.txt: the path decides, so this comes after it.
                .File("/data/contracts/a2.txt", "tie", Base);

            List<PushItem> items = await this.CrawlAsync(cluster);

            Assert.Equal(
                new[]
                {
                    "/data/contracts/a.txt",
                    "/data/contracts/a2.txt",
                    "/data/contracts/b.txt",
                    "/data/contracts/c.txt",
                },
                items.Select(i => (string)i.Properties["itemPath"]).ToArray());
        }

        [Fact]
        public async Task A_resumed_crawl_reads_what_is_after_the_marker_and_nothing_else()
        {
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None).Write(new CrawlCheckpoint
            {
                MarkerTime = Base.AddMinutes(1).UtcDateTime.ToString("o"),
                MarkerKey = "/data/contracts/b.txt",

                // Run 1, so the next is run 2 - not a multiple of the recrawl
                // cadence, so this stays incremental.
                RunCount = 1,
            });

            List<PushItem> items = await this.CrawlAsync(cluster, fullRecrawlEveryRuns: 7, scanSlackSeconds: 0);

            Assert.Equal(
                new[] { "/data/contracts/c.txt" },
                items.Select(i => (string)i.Properties["itemPath"]).ToArray());
        }

        [Fact]
        public async Task The_periodic_full_recrawl_ignores_the_marker()
        {
            // The ACL staleness bound. A permission change does not alter a
            // file's modification time, so without this a revoked grant would
            // never be re-derived.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1));

            new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None).Write(new CrawlCheckpoint
            {
                MarkerTime = Base.AddMinutes(5).UtcDateTime.ToString("o"),
                MarkerKey = "/data/contracts/z.txt",

                // Run 7 done, so the next run is a multiple of the cadence.
                RunCount = 7,
            });

            List<PushItem> items = await this.CrawlAsync(cluster, fullRecrawlEveryRuns: 7);

            Assert.Equal(2, items.Count);
        }

        [Fact]
        public async Task Hadoop_litter_and_unwanted_extensions_are_not_indexed()
        {
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/real.txt", "keep", Base)
                .File("/data/contracts/_SUCCESS", "job marker", Base)
                .File("/data/contracts/.staging", "in progress", Base)
                .File("/data/contracts/part-00000.tmp", "half written", Base)
                .File("/data/contracts/image.png", "binary", Base);

            List<PushItem> items = await this.CrawlAsync(cluster);

            PushItem only = Assert.Single(items);
            Assert.Equal("/data/contracts/real.txt", only.Properties["itemPath"]);
        }

        [Fact]
        public async Task A_file_nobody_can_be_granted_is_skipped_before_its_content_is_read()
        {
            // The important half of this is "before". A document that must not be
            // indexed should never be fetched at all, not fetched and discarded.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/open.txt", "readable", Base, permission: "640", group: "hadoop-contracts-read")
                .File("/data/contracts/secret.txt", "sensitive", Base.AddMinutes(1), permission: "640", group: "unmapped-group");

            List<PushItem> items = await this.CrawlAsync(cluster);

            PushItem only = Assert.Single(items);
            Assert.Equal("/data/contracts/open.txt", only.Properties["itemPath"]);

            Assert.DoesNotContain("OPEN /data/contracts/secret.txt", cluster.Requests);
            Assert.Contains("OPEN /data/contracts/open.txt", cluster.Requests);
        }

        [Fact]
        public async Task A_named_acl_entry_widens_who_may_read_a_file()
        {
            // The file's owning group is not mapped, but an extended ACL entry
            // names one that is - so the cluster grants SOMEBODY, and the file
            // is indexed rather than skipped.
            //
            // Under control ACL-1 this no longer asserts WHO. The derivation
            // still runs and still decides indexability; it no longer composes
            // the grant, because every item carries the connector's single AD
            // group. What survives here is the admission decision, which is the
            // half of the derivation the one-group rule keeps.
            //
            // The mode is 640 because on a file with an extended ACL the middle
            // digit is the ACL MASK, and a named entry grants its own bits AND
            // the mask. At 600 the mask is --- and the cluster grants nothing,
            // which is what CdpAclMaskTests asserts; this test is about the
            // other direction, so it needs a mask that lets the entry through.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File(
                    "/data/contracts/shared.txt",
                    "shared",
                    Base,
                    permission: "640",
                    group: "unmapped-group",
                    "group:hadoop-contracts-read:r--");

            PushItem only = Assert.Single(await this.CrawlAsync(cluster));

            // Admitted, and carrying no ACL of its own: the engine applies the
            // configured group. A null Acl is the mechanism, not an oversight -
            // PushEngine.ResolveAcl branches on it.
            Assert.Null(only.Acl);
        }

        [Fact]
        public async Task A_document_whose_text_cannot_be_extracted_is_still_findable()
        {
            // Indexed by name, path, owner and date, with extractStatus saying
            // why there is no body. A document nobody can find is worse than a
            // document found without its contents.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/report.docx", "this is not really a zip", Base);

            PushItem only = Assert.Single(
                await this.CrawlAsync(cluster, includeExtensions: "txt;docx"));

            Assert.Equal("report.docx", only.Properties["fileName"]);
            Assert.Equal("Failed", only.Properties["extractStatus"]);
            Assert.Equal(string.Empty, only.Content);

            // No ACL of its own, so the engine grants the connector's single AD
            // group. A metadata-only item is not a reason to relax who may see
            // it, and under ACL-1 it does not get the chance to: every admitted
            // item carries exactly the same grant.
            Assert.Null(only.Acl);
        }

        [Fact]
        public async Task Hdfs_refusing_this_identity_is_a_credential_failure_not_an_ingestion_one()
        {
            // Exit 3, not exit 4. An expired ticket must not send an operator
            // into the data path looking for a bug.
            var cluster = new FakeWebHdfs().File("/data/contracts/a.txt", "one", Base);
            cluster.FailWith = (op, path) => HttpStatusCode.Unauthorized;

            await Assert.ThrowsAsync<PushSourceAuthenticationException>(() => this.CrawlAsync(cluster));
        }

        [Fact]
        public async Task An_unreadable_ranger_stops_the_run_rather_than_indexing_anyway()
        {
            // Ranger is what says which paths may be indexed at all. Carrying on
            // without it would be copying data whose access rules are unknown.
            FakeWebHdfs cluster = new FakeWebHdfs().File("/data/contracts/a.txt", "one", Base);

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.CrawlAsync(cluster, rangerStatus: HttpStatusCode.ServiceUnavailable));

            Assert.Contains("Ranger", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_watermark_moves_only_over_items_the_engine_confirmed()
        {
            // The unbreakable rule, at the CDP source rather than at the engine:
            // committing two of three items and stopping leaves the checkpoint on
            // the second, so the third is re-read next run.
            FakeWebHdfs cluster = new FakeWebHdfs()
                .File("/data/contracts/a.txt", "one", Base)
                .File("/data/contracts/b.txt", "two", Base.AddMinutes(1))
                .File("/data/contracts/c.txt", "three", Base.AddMinutes(2));

            var store = new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None);

            await using (IPushSource source = this.Source(cluster))
            {
                var read = new List<PushItem>();

                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    read.Add(item);
                }

                await source.OnItemCommittedAsync(read[0], CancellationToken.None);
                await source.OnItemCommittedAsync(read[1], CancellationToken.None);

                // No OnCrawlCompletedAsync: this is what an interrupted run looks
                // like from the source's side.
            }

            Assert.False(store.Read().HasMarker);

            // Now the same run, completed.
            await using (IPushSource source = this.Source(cluster))
            {
                var read = new List<PushItem>();

                await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
                {
                    read.Add(item);
                }

                foreach (PushItem item in read)
                {
                    await source.OnItemCommittedAsync(item, CancellationToken.None);
                }

                await source.OnCrawlCompletedAsync(CancellationToken.None);
            }

            CrawlCheckpoint after = store.Read();

            Assert.Equal("/data/contracts/c.txt", after.MarkerKey);
            Assert.Equal(1, after.RunCount);
        }

        // ------------------------------------------------------------------
        // Hive
        // ------------------------------------------------------------------

        [Fact]
        public async Task A_hive_item_carries_only_properties_its_schema_declares()
        {
            // Graph rejects a property that is not in the registered schema, so a
            // source stashing its own bookkeeping on the item would fail every
            // write of every watermarked table. The watermark belongs to the
            // source, not to the item, and this is what keeps it there.
            var connector = new CdpGraphPush.HiveContractsConnector();

            var schema = new HashSet<string>(
                connector.BuildSchema().Properties.Select(p => p.Name),
                StringComparer.Ordinal);

            (List<PushItem> items, CheckpointStore store) = await this.ReadHiveAsync();

            Assert.Equal(2, items.Count);

            foreach (PushItem item in items)
            {
                foreach (string property in item.Properties.Keys)
                {
                    Assert.True(
                        schema.Contains(property),
                        $"item {item.Id} carries '{property}', which is not in the registered schema");
                }
            }

            // And the watermark still ends up on the last committed row, which is
            // the thing the properties were being misused for.
            Assert.Equal("C-1001", store.Read().MarkerKey);
        }

        [Fact]
        public async Task A_row_filtered_table_is_never_queried_at_all()
        {
            // Not "queried and discarded". The rows the service account can see
            // are the rows ITS filter admits, so reading them at all would be
            // reading one user's view of the table.
            var reader = new FakeHiveRowReader();

            (List<PushItem> items, _) = await this.ReadHiveAsync(reader: reader, rowFiltered: true);

            Assert.Empty(items);
            Assert.Empty(reader.Queries);
        }

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        [Fact]
        public async Task Text_html_and_open_xml_all_reduce_to_words()
        {
            TextExtractorSet extractors = TextExtractorSet.Default();

            Assert.Equal(
                "hello world",
                (await Extract(extractors, "notes.txt", Encoding.UTF8.GetBytes("hello world"))).Text);

            ExtractionResult html = await Extract(
                extractors,
                "page.html",
                Encoding.UTF8.GetBytes(
                    "<html><head><style>p{color:red}</style></head><body><p>Quarterly&nbsp;report</p>" +
                    "<script>var x=1;</script><p>Second line</p></body></html>"));

            Assert.Contains("Quarterly report", html.Text, StringComparison.Ordinal);
            Assert.Contains("Second line", html.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("color:red", html.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("var x", html.Text, StringComparison.Ordinal);

            ExtractionResult docx = await Extract(extractors, "contract.docx", MinimalDocx("Master services agreement"));

            Assert.Equal(ExtractionStatus.Extracted, docx.Status);
            Assert.Contains("Master services agreement", docx.Text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_unknown_type_and_an_oversized_file_are_statuses_rather_than_failures()
        {
            TextExtractorSet extractors = TextExtractorSet.Default();

            ExtractionResult unknown = await Extract(extractors, "archive.7z", new byte[] { 1, 2, 3 });

            Assert.Equal(ExtractionStatus.Unsupported, unknown.Status);
            Assert.Contains("7z", unknown.Detail, StringComparison.Ordinal);

            bool opened = false;

            ExtractionResult large = await extractors.ExtractAsync(
                _ =>
                {
                    opened = true;
                    return Task.FromResult<Stream>(new MemoryStream());
                },
                "huge.txt",
                sizeBytes: 5_000_000,
                maxRawBytes: 1_000_000,
                CancellationToken.None);

            Assert.Equal(ExtractionStatus.TooLarge, large.Status);

            // The decision is made from the reported size: the file is never
            // opened, never mind streamed.
            Assert.False(opened, "an oversized file must not be opened at all");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static async Task<ExtractionResult> Extract(TextExtractorSet extractors, string name, byte[] bytes)
        {
            return await extractors.ExtractAsync(
                _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
                name,
                bytes.Length,
                maxRawBytes: 10_000_000,
                CancellationToken.None);
        }

        /// <summary>The smallest thing the OOXML extractor should read as a document.</summary>
        private static byte[] MinimalDocx(string text)
        {
            using var buffer = new MemoryStream();

            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry("word/document.xml");

                using Stream stream = entry.Open();
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                writer.Write(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                    "<w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>");
            }

            return buffer.ToArray();
        }

        private async Task<List<PushItem>> CrawlAsync(
            FakeWebHdfs cluster,
            int fullRecrawlEveryRuns = 0,
            int scanSlackSeconds = 0,
            string includeExtensions = "txt;md;html;docx",
            HttpStatusCode rangerStatus = HttpStatusCode.OK)
        {
            var items = new List<PushItem>();

            await using IPushSource source = this.Source(
                cluster, fullRecrawlEveryRuns, scanSlackSeconds, includeExtensions, rangerStatus);

            await foreach (PushItem item in source.ReadAsync(CancellationToken.None))
            {
                items.Add(item);
            }

            return items;
        }

        private IPushSource Source(
            FakeWebHdfs cluster,
            int fullRecrawlEveryRuns = 0,
            int scanSlackSeconds = 0,
            string includeExtensions = "txt;md;html;docx",
            HttpStatusCode rangerStatus = HttpStatusCode.OK)
        {
            PushOptions options = CdpConnectorTests.CdpOptions();
            options.Settings["HdfsBaseUrl"] = cluster.BaseUrl;
            options.Settings["HdfsRoots"] = "/data/contracts";
            options.Settings["IncludeExtensions"] = includeExtensions;
            options.Settings["FullRecrawlEveryRuns"] = fullRecrawlEveryRuns.ToString();
            options.Settings["ScanSlackSeconds"] = scanSlackSeconds.ToString();
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

            CdpSettings settings = CdpSettings.From(options);

            var principals = new PrincipalResolver(
                new Dictionary<string, string> { ["hadoop-contracts-read"] = TestData.GroupObjectId },
                graph: null,
                Logger.None);

            return new HdfsPushSource(
                settings,
                new WebHdfsClient(cluster.BaseUrl, cluster.Client(), Logger.None, ownsClient: true),
                new RangerPolicyClient(
                    "https://ranger.test:6182",
                    new HttpClient(new FakeRanger(rangerStatus)),
                    Logger.None,
                    ownsClient: true),
                new HdfsAclBuilder(principals, string.Empty),
                TextExtractorSet.Default(),
                new CheckpointStore(this.stateDirectory, "cdphdfsdocs", Logger.None),
                Logger.None);
        }

        /// <summary>Runs a Hive source over canned rows and returns what it yielded.</summary>
        private async Task<(List<PushItem> Items, CheckpointStore Store)> ReadHiveAsync(
            FakeHiveRowReader reader = null, bool rowFiltered = false)
        {
            reader ??= new FakeHiveRowReader(
                new Dictionary<string, object>
                {
                    ["contract_ref"] = "C-1000",
                    ["counterparty"] = "Northwind",
                    ["status"] = "Open",
                    ["value_amount"] = 1250.5d,
                    ["last_modified_ts"] = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
                },
                new Dictionary<string, object>
                {
                    ["contract_ref"] = "C-1001",
                    ["counterparty"] = "Contoso",
                    ["status"] = "Under review",
                    ["value_amount"] = 480d,
                    ["last_modified_ts"] = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
                });

            PushOptions options = CdpConnectorTests.CdpOptions();
            options.Source.ItemView = "contracts.contract";
            options.Settings["HiveWatermarkColumn"] = "last_modified_ts";
            options.Settings["HiveKeyColumn"] = "contract_ref";
            options.Settings["CheckpointDirectory"] = this.stateDirectory;

            var policy = new RangerPolicy
            {
                Id = 1,
                PolicyType = rowFiltered ? RangerPolicyType.RowFilter : RangerPolicyType.Access,
                Enabled = true,
            };

            policy.Resources["database"] = new List<string> { "contracts" };
            policy.Resources["table"] = new List<string> { "contract" };

            var item = new RangerPolicyItem();
            item.Accesses.Add("select");
            item.Groups.Add("hadoop-contracts-read");
            policy.Allow.Add(item);

            var store = new CheckpointStore(this.stateDirectory, "cdphivecontracts", Logger.None);

            var source = new CdpConnector.Source.Hive.HivePushSource(
                CdpSettings.From(options),
                options,
                reader,
                new RoutingEvaluator(new[] { policy }),
                rowFiltered
                    ? Array.Empty<PushAclEntry>()
                    : new[] { new PushAclEntry(PushAclType.Group, TestData.GroupObjectId) },
                store,
                CdpGraphPush.HiveContractsConnector.MapRow,
                Logger.None);

            var items = new List<PushItem>();

            await using (source)
            {
                await foreach (PushItem yielded in source.ReadAsync(CancellationToken.None))
                {
                    items.Add(yielded);
                }

                foreach (PushItem yielded in items)
                {
                    await source.OnItemCommittedAsync(yielded, CancellationToken.None);
                }

                await source.OnCrawlCompletedAsync(CancellationToken.None);
            }

            return (items, store);
        }

        /// <summary>Canned rows, and a record of the queries it was asked to run.</summary>
        private sealed class FakeHiveRowReader : CdpConnector.Source.Hive.IHiveRowReader
        {
            private readonly List<Dictionary<string, object>> rows;

            public FakeHiveRowReader(params Dictionary<string, object>[] rows)
            {
                this.rows = rows.ToList();
            }

            public List<string> Queries { get; } = new List<string>();

            public async IAsyncEnumerable<CdpConnector.Source.Hive.HiveRow> QueryAsync(
                string query,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                this.Queries.Add(query);

                foreach (Dictionary<string, object> row in this.rows)
                {
                    await Task.Yield();
                    yield return new CdpConnector.Source.Hive.HiveRow(row);
                }
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>A Ranger with no policies, or one that will not answer.</summary>
        private sealed class FakeRanger : HttpMessageHandler
        {
            private readonly HttpStatusCode status;

            public FakeRanger(HttpStatusCode status)
            {
                this.status = status;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (this.status != HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(this.status));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
