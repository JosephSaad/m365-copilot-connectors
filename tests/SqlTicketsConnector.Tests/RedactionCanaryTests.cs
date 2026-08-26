// ===========================================================================
// RedactionCanaryTests.cs
//
//   CONTROL EVIDENCE. Do not delete or weaken these tests.
//
//   They are the evidence that item content, property values, secrets and
//   connection strings never reach a log sink. ControlEvidenceTests fails the
//   build if the canary test below is renamed or removed, so deleting this file
//   breaks the suite loudly rather than quietly removing a security control.
//
//   Control mapping: docs/SECURITY.md, LOG-3 and LOG-4.
// ===========================================================================

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog.Core;
    using Serilog.Events;
    using SqlTicketsConnector.Connector;
    using global::Connector.Security.Logging;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class RedactionCanaryTests
    {
        /// <summary>
        /// A string that appears only in ticket data. If it ever shows up in a log
        /// event, customer data is reaching the log file.
        /// </summary>
        private const string Canary = "CANARY-9d41b2f7-customer-record";

        [Fact]
        public async Task Crawl_does_not_leak_row_content_into_logs()
        {
            var sink = new CollectingSink();

            var rows = new List<TicketRow>
            {
                new TicketRow
                {
                    TicketId = 1001,
                    Title = "Account query " + Canary,
                    Status = Canary,
                    AssignedTo = Canary + "@contoso.com",
                    Body = "Customer said: " + Canary,
                    LastModifiedUtc = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
                },
                new TicketRow
                {
                    TicketId = 1002,
                    Title = "Follow up",
                    Status = "Open",
                    AssignedTo = "agent@contoso.com",
                    Body = new string('x', 64) + Canary,
                    LastModifiedUtc = new DateTime(2026, 8, 13, 9, 5, 0, DateTimeKind.Utc),
                    IsDeleted = true,
                },
            };

            using (Logger logger = TestData.RedactingLogger(sink))
            {
                var service = new ConnectorCrawlerServiceImpl(
                    new FakeTicketSource(rows),
                    TestData.ValidOptions(),
                    logger);

                var incremental = new FakeStreamWriter<IncrementalCrawlStreamBit>();

                await service.GetIncrementalCrawlStream(
                    new GetIncrementalCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                    incremental,
                    new FakeServerCallContext("/x/GetIncrementalCrawlStream", CancellationToken.None));

                var full = new FakeStreamWriter<CrawlStreamBit>();

                await service.GetCrawlStream(
                    new GetCrawlStreamRequest { Schema = SqlDataSource.BuildSchema() },
                    full,
                    new FakeServerCallContext("/x/GetCrawlStream", CancellationToken.None));

                // The crawl really did run and really did carry the canary in the
                // items, so the assertion below is about redaction, not about an
                // empty test.
                Assert.Contains(
                    full.Written.Where(bit => bit.CrawlItem != null),
                    bit => bit.CrawlItem.ContentItem.Content.ContentValue.Contains(Canary, StringComparison.Ordinal));
            }

            Assert.NotEmpty(sink.Events);

            // Item IDs are allowed and expected. Values are not.
            Assert.Contains(sink.Events, e => Text(e).Contains("ticket1001", StringComparison.Ordinal));

            foreach (LogEvent logEvent in sink.Events)
            {
                Assert.DoesNotContain(Canary, Text(logEvent), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Destructuring_a_row_or_an_item_yields_counts_and_sizes_only()
        {
            var sink = new CollectingSink();

            var row = new TicketRow
            {
                TicketId = 7,
                Title = Canary,
                Body = Canary,
                Status = Canary,
                AssignedTo = Canary,
                LastModifiedUtc = DateTime.UtcNow,
            };

            using (Logger logger = TestData.RedactingLogger(sink))
            {
                var builder = new CrawlItemBuilder(
                    new[] { TestData.GroupObjectId },
                    4096,
                    "https://tickets.contoso.com/ticket/{0}",
                    Logger.None);

                BuiltItem built = builder.Build(row, SqlDataSource.BuildSchema(), null);

                logger.Information("Row {@Row}", row);
                logger.Information("Item {@Item}", built.ContentItem);
                logger.Information("Crawl item {@CrawlItem}", new CrawlItem
                {
                    ItemId = built.ItemId,
                    ItemType = CrawlItem.Types.ItemType.ContentItem,
                    ContentItem = built.ContentItem,
                });

                // The plain hole is the dangerous one: it renders through ToString(),
                // which for a protobuf message is full JSON.
                logger.Information("Raw {Item}", built.ContentItem);
            }

            foreach (LogEvent logEvent in sink.Events)
            {
                Assert.DoesNotContain(Canary, Text(logEvent), StringComparison.Ordinal);
            }

            Assert.Contains(sink.Events, e => Text(e).Contains("ContentBytes", StringComparison.Ordinal));
        }

        [Fact]
        public void A_connection_string_never_reaches_a_sink_in_either_form()
        {
            var sink = new CollectingSink();

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = "sql01.contoso.local",
                InitialCatalog = "Ops",
                UserID = "svc_gca_reader",
                Password = Canary,
                Encrypt = true,
            };

            using (Logger logger = TestData.RedactingLogger(sink))
            {
                logger.Information("Structured {@Builder}", builder);
                logger.Information("Plain {Builder}", builder.ConnectionString);
                logger.Information("Interpolated {Text}", "Connecting with " + builder.ConnectionString);
            }

            foreach (LogEvent logEvent in sink.Events)
            {
                string text = Text(logEvent);
                Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
                Assert.DoesNotContain("svc_gca_reader", text, StringComparison.Ordinal);
            }

            // Server and database stay visible: they are what an operator needs.
            Assert.Contains(sink.Events, e => Text(e).Contains("sql01.contoso.local", StringComparison.Ordinal));
        }

        [Fact]
        public void Exception_text_is_scrubbed_before_it_is_written()
        {
            var inner = new InvalidOperationException(
                "Server=sql01;Database=Ops;User ID=svc;Password=" + Canary + ";Encrypt=True");

            Exception wrapped = RedactedException.Wrap(new InvalidOperationException("Open failed.", inner));

            string rendered = wrapped.ToString();

            Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
            Assert.Contains("redacted", rendered, StringComparison.OrdinalIgnoreCase);

            // A benign exception is passed through untouched, so stack traces stay
            // exactly as the runtime produced them.
            var benign = new TimeoutException("Timeout expired.");
            Assert.Same(benign, RedactedException.Wrap(benign));
        }

        [Fact]
        public void Tokens_and_private_keys_are_scrubbed()
        {
            const string Jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJl";

            Assert.DoesNotContain(Jwt, LogScrubber.Scrub("Authorization: Bearer " + Jwt), StringComparison.Ordinal);
            Assert.DoesNotContain(
                "MIIEvQIBADANBg",
                LogScrubber.Scrub("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBg\n-----END PRIVATE KEY-----"),
                StringComparison.Ordinal);

            Assert.Equal("nothing to see here", LogScrubber.Scrub("nothing to see here"));
        }

        /// <summary>Renders everything a sink could write for one event.</summary>
        private static string Text(LogEvent logEvent)
        {
            var builder = new StringBuilder();
            builder.AppendLine(logEvent.RenderMessage());

            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                builder.Append(property.Key).Append('=').AppendLine(property.Value.ToString());
            }

            if (logEvent.Exception != null)
            {
                builder.AppendLine(logEvent.Exception.ToString());
            }

            return builder.ToString();
        }
    }
}
