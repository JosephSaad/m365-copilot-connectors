// ---------------------------------------------------------------------------
// TeradataConnectorTests.cs
// The Teradata direct-push connector: its dialect, its connection string, its
// configuration rules and its row mapping.
//
// The dialect difference worth pinning is the row cap. Teradata's TOP binds
// BEFORE ORDER BY and Oracle's FETCH FIRST binds after, so the two connectors
// emit the clause in opposite positions from the same MaxItems setting. Getting
// either backwards caps an unordered read, which returns a different arbitrary
// subset on every crawl and looks like data loss rather than a query bug.
// ---------------------------------------------------------------------------

#nullable enable

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using SqlTicketsConnector.Tests.TestSupport;
    using TeradataGraphPush;
    using Xunit;

    public class TeradataConnectorTests
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
            options.DataSource.Server = "td01.contoso.local";
            options.DataSource.Database = "APP_DB";
            options.DataSource.SqlAuthMode = authMode;
            options.DataSource.SqlUserId = "graph_reader";
            options.DataSource.SoftDeleteEnabled = softDelete;
            options.DataSource.ItemUrlTemplate = "https://records.contoso.com/record/{0}";
            return options;
        }

        [Fact]
        public void The_schema_matches_the_other_relational_connectors()
        {
            string[] expected = { "recordId", "title", "status", "owner", "lastModified", "url" };

            Assert.Equal(
                expected,
                new TeradataRecordsPushConnector().BuildSchema().Properties!.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void The_schema_carries_the_three_semantic_labels_Copilot_needs()
        {
            List<Label?> labels = new TeradataRecordsPushConnector().BuildSchema().Properties!
                .SelectMany(p => p.Labels ?? new List<Label?>())
                .ToList();

            Assert.Contains(Label.Title, labels);
            Assert.Contains(Label.Url, labels);
            Assert.Contains(Label.LastModifiedDateTime, labels);
        }

        [Fact]
        public void A_row_cap_is_expressed_as_TOP_before_the_ORDER_BY()
        {
            // The opposite of Oracle. See the file header.
            string sql = new TeradataRecordsPushConnector().BuildQuery(Options(maxItems: 25));

            int top = sql.IndexOf("TOP 25", StringComparison.Ordinal);
            int order = sql.IndexOf("ORDER BY", StringComparison.Ordinal);

            Assert.True(top >= 0, "the cap must be expressed as TOP");
            Assert.True(order > top, "ORDER BY must follow TOP");
            Assert.DoesNotContain("FETCH FIRST", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void No_row_cap_emits_no_TOP_clause()
        {
            Assert.DoesNotContain(
                "TOP ",
                new TeradataRecordsPushConnector().BuildQuery(Options(maxItems: 0)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_soft_delete_filter_is_applied_when_enabled_and_absent_when_not()
        {
            var connector = new TeradataRecordsPushConnector();

            Assert.Contains(
                "WHERE IS_DELETED = 0",
                connector.BuildQuery(Options(softDelete: true)),
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                "IS_DELETED",
                connector.BuildQuery(Options(softDelete: false)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_query_is_always_ordered_so_a_capped_read_is_deterministic()
        {
            Assert.Contains(
                "ORDER BY RECORD_ID",
                new TeradataRecordsPushConnector().BuildQuery(Options()),
                StringComparison.Ordinal);
        }

        [Fact]
        public void A_password_connection_uses_TD2_and_carries_the_secret()
        {
            var connector = new TeradataRecordsPushConnector();
            PushOptions options = Options(authMode: "SqlLogin");
            connector.ValidateOptions(options, new ValidationErrors());

            string cs = connector.BuildConnectionString(options, "s3cret");

            Assert.Contains("TD2", cs, StringComparison.Ordinal);
            Assert.Contains("s3cret", cs, StringComparison.Ordinal);
            Assert.Equal(TeradataRecordsPushConnector.PasswordKey, connector.SecretKey);
        }

        [Fact]
        public void An_integrated_connection_uses_Kerberos_and_carries_no_credential()
        {
            var connector = new TeradataRecordsPushConnector();
            PushOptions options = Options(authMode: "Integrated");
            connector.ValidateOptions(options, new ValidationErrors());

            string cs = connector.BuildConnectionString(options, null);

            Assert.Contains("KRB5", cs, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret", cs, StringComparison.Ordinal);
            Assert.Null(connector.SecretKey);
        }

        [Fact]
        public void A_missing_server_is_reported_against_its_own_key()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options();
            options.DataSource.Server = string.Empty;

            new TeradataRecordsPushConnector().ValidateOptions(options, errors);

            Assert.Contains("DataSource:Server", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_password_mode_without_a_user_is_refused()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options(authMode: "SqlLogin");
            options.DataSource.SqlUserId = string.Empty;

            new TeradataRecordsPushConnector().ValidateOptions(options, errors);

            Assert.Contains("DataSource:SqlUserId", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_BYTEINT_key_arriving_as_a_byte_maps_rather_than_throwing()
        {
            // Teradata's small integer types do not arrive as Int32 either. Same
            // class of defect as Oracle's NUMBER, same reason DbRead converts.
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = (byte)9,
                ["TITLE"] = "A record",
                ["STATUS"] = "Open",
                ["OWNER"] = "jsmith",
                ["BODY"] = "The body",
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            });

            PushItem? item = new TeradataRecordsPushConnector().MapRow(reader, Options());

            Assert.NotNull(item);
            Assert.Equal("teradatarecord9", item!.Id);
            Assert.Equal("9", (string)item.Properties["recordId"]);
        }

        [Fact]
        public void A_null_key_is_skipped_rather_than_collapsed_onto_one_item()
        {
            var reader = new FakeDbDataReader(new Dictionary<string, object?>
            {
                ["RECORD_ID"] = null,
                ["TITLE"] = "No key",
                ["STATUS"] = "Open",
                ["OWNER"] = "jsmith",
                ["BODY"] = "Body",
                ["LAST_MODIFIED"] = DateTime.UtcNow,
            });

            Assert.Null(new TeradataRecordsPushConnector().MapRow(reader, Options()));
        }

        [Fact]
        public void The_item_identifier_does_not_collide_with_the_Oracle_connectors()
        {
            // Two connectors writing into one tenant must not be able to compose
            // the same item ID from the same key, or one overwrites the other
            // through the PUT upsert.
            var row = new Dictionary<string, object?>
            {
                ["RECORD_ID"] = 5m,
                ["TITLE"] = "t",
                ["STATUS"] = "s",
                ["OWNER"] = "o",
                ["BODY"] = "b",
                ["LAST_MODIFIED"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            PushItem? teradata = new TeradataRecordsPushConnector()
                .MapRow(new FakeDbDataReader(row), Options());

            PushItem? oracle = new OracleGraphPush.OracleRecordsPushConnector()
                .MapRow(new FakeDbDataReader(row), Options());

            Assert.NotEqual(oracle!.Id, teradata!.Id);
        }
    }
}
