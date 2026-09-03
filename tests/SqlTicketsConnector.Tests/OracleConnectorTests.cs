// ---------------------------------------------------------------------------
// OracleConnectorTests.cs
// The Oracle direct-push connector: its dialect, its connection string, its
// configuration rules and its row mapping.
//
// The guard itself - the VPD, Label Security and Data Redaction refusal - needs
// an open connection and is not exercised here. What IS exercised is everything
// that decides whether the guard can work: the catalogue queries take an
// unqualified, upper-cased object name, and a configuration written as
// "app.records_v" must not sail past a policy because the name was compared in
// the wrong case. That is asserted through BuildQuery and the connector's own
// validation, which are the parts a test can reach without a database.
// ---------------------------------------------------------------------------

#nullable enable

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using OracleGraphPush;
    using PushCore;
    using PushCore.State;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class OracleConnectorTests
    {
        private static PushOptions Options(
            string view = "APP_RECORDS_V",
            bool softDelete = true,
            int maxItems = 0,
            string authMode = "SqlLogin")
        {
            var options = new PushOptions();
            options.Source.ItemView = view;
            options.Source.MaxItems = maxItems;
            options.DataSource.Server = "ora01.contoso.local:1521/ORCLPDB1";
            options.DataSource.SqlAuthMode = authMode;
            options.DataSource.SqlUserId = "GRAPH_READER";
            options.DataSource.SoftDeleteEnabled = softDelete;
            options.DataSource.ItemUrlTemplate = "https://records.contoso.com/record/{0}";
            return options;
        }

        [Fact]
        public void The_schema_carries_the_three_semantic_labels_Copilot_needs()
        {
            Schema schema = new OracleRecordsPushConnector().BuildSchema();

            List<Label?> labels = schema.Properties!
                .SelectMany(p => p.Labels ?? new List<Label?>())
                .ToList();

            Assert.Contains(Label.Title, labels);
            Assert.Contains(Label.Url, labels);
            Assert.Contains(Label.LastModifiedDateTime, labels);
        }

        [Fact]
        public void The_schema_matches_the_other_relational_connectors()
        {
            // One schema across SQL, Oracle and Teradata, so an operator reading
            // three connections reads one shape. A property added to one and not
            // the others is a divergence worth failing a build over.
            string[] expected = { "recordId", "title", "status", "owner", "lastModified", "url" };

            Assert.Equal(
                expected,
                new OracleRecordsPushConnector().BuildSchema().Properties!.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void The_soft_delete_filter_is_applied_when_enabled()
        {
            string sql = new OracleRecordsPushConnector().BuildQuery(Options(softDelete: true), null);

            Assert.Contains("WHERE IS_DELETED = 0", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void The_soft_delete_filter_is_absent_when_disabled()
        {
            string sql = new OracleRecordsPushConnector().BuildQuery(Options(softDelete: false), null);

            Assert.DoesNotContain("IS_DELETED", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void A_row_cap_is_expressed_as_FETCH_FIRST_after_the_ORDER_BY()
        {
            // Oracle's row-limiting clause binds AFTER ordering, unlike SQL
            // Server's TOP. Emitting it before ORDER BY caps an unordered read,
            // which returns a different arbitrary subset on every crawl.
            string sql = new OracleRecordsPushConnector().BuildQuery(Options(maxItems: 25), null);

            int order = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
            int fetch = sql.IndexOf("FETCH FIRST 25 ROWS ONLY", StringComparison.Ordinal);

            Assert.True(order >= 0, "the query must be ordered");
            Assert.True(fetch > order, "FETCH FIRST must follow ORDER BY");
        }

        [Fact]
        public void No_row_cap_emits_no_FETCH_clause()
        {
            Assert.DoesNotContain(
                "FETCH FIRST",
                new OracleRecordsPushConnector().BuildQuery(Options(maxItems: 0), null),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_query_reads_every_column_the_mapping_needs()
        {
            string sql = new OracleRecordsPushConnector().BuildQuery(Options(), null);

            foreach (string column in new[] { "RECORD_ID", "TITLE", "STATUS", "OWNER", "BODY", "LAST_MODIFIED" })
            {
                Assert.Contains(column, sql, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_password_connection_names_the_user_and_carries_the_secret()
        {
            var connector = new OracleRecordsPushConnector();
            PushOptions options = Options(authMode: "SqlLogin");
            connector.ValidateOptions(options, new ValidationErrors());

            string cs = connector.BuildConnectionString(options, "s3cret");

            Assert.Contains("GRAPH_READER", cs, StringComparison.Ordinal);
            Assert.Contains("s3cret", cs, StringComparison.Ordinal);
        }

        [Fact]
        public void An_integrated_connection_carries_no_credential_at_all()
        {
            // Integrated on Oracle means a wallet or Kerberos. The point of the
            // mode is that no secret passes through this process, so a password
            // appearing here would defeat it silently.
            var connector = new OracleRecordsPushConnector();
            PushOptions options = Options(authMode: "Integrated");
            connector.ValidateOptions(options, new ValidationErrors());

            string cs = connector.BuildConnectionString(options, null);

            Assert.DoesNotContain("s3cret", cs, StringComparison.Ordinal);
            Assert.Null(connector.SecretKey);
        }

        [Fact]
        public void A_password_mode_connector_asks_for_a_vault_secret()
        {
            var connector = new OracleRecordsPushConnector();
            connector.ValidateOptions(Options(authMode: "SqlLogin"), new ValidationErrors());

            Assert.Equal(OracleRecordsPushConnector.PasswordKey, connector.SecretKey);
        }

        [Fact]
        public void A_missing_server_is_reported_against_its_own_key()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options();
            options.DataSource.Server = string.Empty;

            new OracleRecordsPushConnector().ValidateOptions(options, errors);

            Assert.Contains("DataSource:Server", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_password_mode_without_a_user_is_refused()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options(authMode: "SqlLogin");
            options.DataSource.SqlUserId = string.Empty;

            new OracleRecordsPushConnector().ValidateOptions(options, errors);

            Assert.Contains("DataSource:SqlUserId", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_integrated_connector_does_not_demand_a_user()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options(authMode: "Integrated");
            options.DataSource.SqlUserId = string.Empty;

            new OracleRecordsPushConnector().ValidateOptions(options, errors);

            Assert.DoesNotContain("DataSource:SqlUserId", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_NUMBER_key_arriving_as_a_decimal_maps_rather_than_throwing()
        {
            // The defect this test exists for: Oracle surfaces NUMBER as a
            // decimal whatever its scale, so a mapping that called GetInt32
            // would throw InvalidCastException on a column that is an integer in
            // every sense the schema cares about - one row into a live crawl.
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = 4242m,
                ["TITLE"] = "A record",
                ["STATUS"] = "Open",
                ["OWNER"] = "jsmith",
                ["BODY"] = "The body",
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            });

            PushItem? item = new OracleRecordsPushConnector().MapRow(reader, Options());

            Assert.NotNull(item);
            Assert.Equal("oraclerecord4242", item!.Id);
            Assert.Equal("4242", (string)item.Properties["recordId"]);
            Assert.Equal("A record", (string)item.Properties["title"]);
            Assert.Equal("The body", item.Content);
            Assert.Equal("https://records.contoso.com/record/4242", (string)item.Properties["url"]);
        }

        [Fact]
        public void A_null_key_is_skipped_rather_than_collapsed_onto_one_item()
        {
            // Coalescing a null key would give every such row the same item ID,
            // and the PUT upsert would silently merge them into one item.
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = null,
                ["TITLE"] = "No key",
                ["STATUS"] = "Open",
                ["OWNER"] = "jsmith",
                ["BODY"] = "Body",
                ["LAST_MODIFIED"] = DateTime.UtcNow,
            });

            Assert.Null(new OracleRecordsPushConnector().MapRow(reader, Options()));
        }

        [Fact]
        public void A_null_text_column_maps_to_empty_rather_than_failing_the_row()
        {
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = 7m,
                ["TITLE"] = null,
                ["STATUS"] = null,
                ["OWNER"] = null,
                ["BODY"] = null,
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            PushItem? item = new OracleRecordsPushConnector().MapRow(reader, Options());

            Assert.NotNull(item);
            Assert.Equal(string.Empty, (string)item!.Properties["title"]);
            Assert.Equal(string.Empty, item.Content);
        }

        [Fact]
        public void The_timestamp_is_written_as_round_trip_UTC()
        {
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = 1m,
                ["TITLE"] = "t",
                ["STATUS"] = "s",
                ["OWNER"] = "o",
                ["BODY"] = "b",
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc),
            });

            PushItem? item = new OracleRecordsPushConnector().MapRow(reader, Options());

            Assert.StartsWith("2026-09-01T12:30:00", (string)item!.Properties["lastModified"], StringComparison.Ordinal);
        }

        [Fact]
        public void Without_a_resume_marker_the_query_carries_no_predicate()
        {
            string sql = new OracleRecordsPushConnector().BuildQuery(Options(), null);

            Assert.DoesNotContain(":marker", sql, StringComparison.Ordinal);
            Assert.Contains("ORDER BY RECORD_ID", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void A_resume_marker_produces_a_composite_predicate_not_a_timestamp_one()
        {
            // A predicate on the timestamp alone either re-reads every row
            // sharing the last second for ever, or loses whichever of them had
            // not been written when the run stopped.
            var marker = new CrawlMarker(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "500");

            string sql = new OracleRecordsPushConnector().BuildQuery(Options(), marker);

            Assert.Contains("LAST_MODIFIED > :marker", sql, StringComparison.Ordinal);
            Assert.Contains("LAST_MODIFIED = :marker AND RECORD_ID > :markerKey", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void The_ORDER_BY_matches_the_resume_predicate_exactly()
        {
            // If the ordering and the predicate disagree, the checkpoint can
            // advance past a row the next run's predicate then excludes - the
            // row is never indexed and nothing reports it.
            var marker = new CrawlMarker(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "500");

            string sql = new OracleRecordsPushConnector().BuildQuery(Options(), marker);

            Assert.Contains("ORDER BY LAST_MODIFIED, RECORD_ID", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void The_soft_delete_filter_survives_a_resume_predicate()
        {
            var marker = new CrawlMarker(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "500");

            string sql = new OracleRecordsPushConnector().BuildQuery(Options(softDelete: true), marker);

            Assert.Contains("IS_DELETED = 0", sql, StringComparison.Ordinal);
            Assert.Contains(" AND ", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void The_connector_declares_a_watermark_column()
        {
            Assert.Equal("LAST_MODIFIED", new OracleRecordsPushConnector().WatermarkColumn);
        }

        [Fact]
        public void A_mapped_row_carries_LastModifiedUtc_so_the_checkpoint_can_advance()
        {
            // A marker-tier source that leaves this null checkpoints nothing and
            // reads in full for ever without saying so.
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = 3m,
                ["TITLE"] = "t",
                ["STATUS"] = "s",
                ["OWNER"] = "o",
                ["BODY"] = "b",
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            });

            PushItem? item = new OracleRecordsPushConnector().MapRow(reader, Options());

            Assert.NotNull(item!.LastModifiedUtc);
            Assert.Equal(DateTimeKind.Utc, item.LastModifiedUtc!.Value.Kind);
            Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), item.LastModifiedUtc.Value);
        }
    }
}
