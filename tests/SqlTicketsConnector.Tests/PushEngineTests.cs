// ---------------------------------------------------------------------------
// PushEngineTests.cs
// The shared engine, and the promise it makes: a new SQL source is a class and
// a configuration file, with no change to PushCore and no effect on the
// connectors already there.
//
// SampleConnector below is that promise, executed. It is defined here, in the
// test assembly, referencing nothing but the public interface — so if adding a
// connector ever required editing the core, this file would stop compiling.
//
// What is not covered, and cannot be without a database: MapRow. SqlDataReader
// is sealed with no interface behind it, so a row cannot be faked. The queries
// those mappings read are asserted instead, and the mapping itself is exercised
// by a --dry-run against a real source. The half of the loop that does not need
// a database - what the engine does with an item once it has one, and when it
// tells the source that item counted - is covered in PushSourceTests.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Microsoft.Graph.Models.ExternalConnectors;
    using SqlGraphPush;
    using SqlHierarchyPush;
    using Serilog;
    using Serilog.Core;
    using Serilog.Events;
    using PushCore;
    using PushCore.Sql;
    using global::Connector.Security.Configuration;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushEngineTests
    {
        /// <summary>
        /// A connector written the way a future one would be. Nothing in
        /// PushCore knows it exists, and nothing had to be changed to add it.
        /// </summary>
        private sealed class SampleConnector : ISqlPushConnector
        {
            public string Key => "invoices";

            public string DisplayName => "Invoices";

            public string DefaultConnectionId => "sampleinvoices";

            public string DefaultConnectionName => "Sample invoices";

            public string DefaultItemView => "dbo.vwInvoices";

            public Schema BuildSchema()
            {
                return PushSchema.Of(
                    PushSchema.Prop("title", PropertyType.String, searchable: true, retrievable: true,
                        label: Label.Title),
                    PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
                    PushSchema.Prop("amount", PropertyType.Double, queryable: true, retrievable: true));
            }

            public string BuildQuery(PushOptions options)
            {
                return $"SELECT InvoiceId, Title, Amount FROM {options.Source.ItemView} ORDER BY InvoiceId;";
            }

            public PushItem MapRow(SqlDataReader reader, PushOptions options)
            {
                return null;
            }
        }

        [Fact]
        public void A_new_connector_is_a_class_and_a_configuration_file_and_nothing_else()
        {
            // The whole requirement, in one test. SampleConnector is defined in
            // this file, implements only the public interface, and gets a schema
            // builder, defaults, validation and a configuration file of its own
            // without a line changing anywhere in PushCore.
            var connector = new SampleConnector();
            var options = new PushOptions();

            PushHost.ApplyDefaults(options, connector);

            Assert.Equal("sampleinvoices", options.Graph.ConnectionId);
            Assert.Equal("Sample invoices", options.Graph.ConnectionName);
            Assert.Equal("dbo.vwInvoices", options.Source.ItemView);
            Assert.Contains("dbo.vwInvoices", connector.BuildQuery(options), StringComparison.Ordinal);

            // Its schema goes through the same guard as everyone else's.
            Schema schema = connector.BuildSchema();
            Assert.Equal(new[] { "title", "url", "amount" }, schema.Properties.Select(p => p.Name).ToArray());
            Assert.Equal("microsoft.graph.externalItem", schema.BaseType);
        }

        [Fact]
        public void Adding_a_connector_does_not_disturb_the_ones_already_there()
        {
            var connectors = new IPushConnector[]
            {
                new TicketsPushConnector(), new HierarchyPushConnector(), new SampleConnector(),
            };

            // Each keeps its own connection and its own configuration file.
            Assert.Equal(
                connectors.Length,
                connectors.Select(c => c.DefaultConnectionId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(
                connectors.Length,
                connectors.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // And the newcomer cannot be pointed at a neighbour's connection
            // without the host saying so — a rule nobody had to teach it.
            var errors = new ValidationErrors();
            PushHost.RejectNeighboursConnection(
                TestData.ValidPushOptions("consultingwork"), new SampleConnector(), connectors, errors);

            Assert.True(errors.HasErrors);
            Assert.Contains(errors.Errors, e => e.Contains("'consultingwork' connector", StringComparison.Ordinal));
        }

        [Fact]
        public void Selecting_a_connector_is_optional_when_an_executable_hosts_one()
        {
            var single = new IPushConnector[] { new TicketsPushConnector() };

            IPushConnector chosen = PushConnectorRegistry.Select(single, null, out string problem);

            Assert.NotNull(chosen);
            Assert.Equal(string.Empty, problem);
            Assert.Equal("tickets", chosen.Key);
        }

        [Fact]
        public void Selecting_is_required_when_an_executable_hosts_more_than_one()
        {
            var many = new IPushConnector[] { new TicketsPushConnector(), new SampleConnector() };

            Assert.Null(PushConnectorRegistry.Select(many, null, out string ambiguous));
            Assert.Contains("--connector is required", ambiguous, StringComparison.Ordinal);
            Assert.Contains("tickets", ambiguous, StringComparison.Ordinal);
            Assert.Contains("invoices", ambiguous, StringComparison.Ordinal);

            Assert.Null(PushConnectorRegistry.Select(many, "nosuch", out string unknown));
            Assert.Contains("No connector named 'nosuch'", unknown, StringComparison.Ordinal);

            // Named, and case does not matter.
            Assert.Equal("invoices", PushConnectorRegistry.Select(many, "INVOICES", out _)!.Key);
        }

        [Fact]
        public void Each_executable_hosts_exactly_the_connector_it_is_named_for()
        {
            IReadOnlyList<IPushConnector> tickets =
                PushConnectorRegistry.Discover(typeof(TicketsPushConnector).Assembly);
            IReadOnlyList<IPushConnector> hierarchy =
                PushConnectorRegistry.Discover(typeof(HierarchyPushConnector).Assembly);

            Assert.Equal(new[] { "tickets" }, tickets.Select(c => c.Key).ToArray());
            Assert.Equal(new[] { "consultingwork" }, hierarchy.Select(c => c.Key).ToArray());
        }

        [Fact]
        public void The_hierarchy_query_reads_the_configured_view_and_orders_parents_first()
        {
            // A run interrupted halfway must leave customers and engagements
            // present with time entries missing — a coherent index — rather than
            // orphaned children whose ancestors are not there to be found.
            string query = new HierarchyPushConnector().BuildQuery(TestData.ValidPushOptions());

            Assert.Contains("FROM dbo.vwExternalItems", query, StringComparison.Ordinal);
            Assert.Contains(
                "ORDER BY CASE ItemType WHEN 'Customer' THEN 0 WHEN 'Engagement' THEN 1 ELSE 2 END",
                query,
                StringComparison.Ordinal);
            Assert.DoesNotContain("TOP (", query, StringComparison.Ordinal);

            PushOptions capped = TestData.ValidPushOptions();
            capped.Source.MaxItems = 25;

            Assert.Contains("SELECT TOP (25) ", new HierarchyPushConnector().BuildQuery(capped), StringComparison.Ordinal);
        }

        [Fact]
        public void The_ticket_query_excludes_soft_deleted_rows_only_when_the_column_exists()
        {
            PushOptions options = TestData.ValidPushOptions("sqltickets", "dbo.Tickets");
            options.DataSource.SoftDeleteEnabled = true;

            string withColumn = new TicketsPushConnector().BuildQuery(options);

            Assert.Contains("FROM dbo.Tickets", withColumn, StringComparison.Ordinal);
            Assert.Contains("WHERE IsDeleted = 0", withColumn, StringComparison.Ordinal);

            options.DataSource.SoftDeleteEnabled = false;

            Assert.DoesNotContain(
                "IsDeleted", new TicketsPushConnector().BuildQuery(options), StringComparison.Ordinal);
        }

        [Fact]
        public void A_connection_carrying_a_foreign_schema_is_refused_before_any_write()
        {
            // The cross-connector guard, connector-name-agnostic by design: no
            // connector names another's connection ID; the engine compares what
            // is registered against what THIS connector builds. Pointing the
            // hierarchy tool at the tickets connection - or at any connection
            // some future connector registers - fails here, before the upsert
            // can overwrite a single foreign item.
            Schema hierarchy = new HierarchyPushConnector().BuildSchema();
            Schema tickets = new TicketsPushConnector().BuildSchema();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => PushEngine.VerifySchemaOwnership("someconnection", hierarchy, tickets, Logger.None));

            Assert.Contains("someconnection", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("ticketId", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("another connector", thrown.Message, StringComparison.Ordinal);

            // And the mirror image: the tickets tool against the hierarchy schema.
            Assert.Throws<InvalidOperationException>(
                () => PushEngine.VerifySchemaOwnership("other", tickets, hierarchy, Logger.None));
        }

        [Fact]
        public void A_connections_own_schema_passes_the_ownership_check()
        {
            Schema schema = new HierarchyPushConnector().BuildSchema();
            var sink = new CollectingSink();

            using (Serilog.Core.Logger log = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink)
                .CreateLogger())
            {
                // Identical: the normal re-run, and no warning about it.
                PushEngine.VerifySchemaOwnership("consultingwork", schema, schema, log);
                Assert.Empty(sink.Events);

                // Older connection missing a property this connector has since
                // added: append-only evolution - never fatal, but WARNED, with
                // the pending property named so the operator adds it on purpose.
                string dropped = schema.Properties[^1].Name;
                var older = new Schema
                {
                    BaseType = schema.BaseType,
                    Properties = schema.Properties.Take(schema.Properties.Count - 1).ToList(),
                };

                PushEngine.VerifySchemaOwnership("consultingwork", schema, older, log);

                LogEvent warning = Assert.Single(sink.Events);
                Assert.Equal(LogEventLevel.Warning, warning.Level);
                Assert.Contains(dropped, warning.RenderMessage(), StringComparison.OrdinalIgnoreCase);

                // Unreadable or empty registered schema: nothing to compare.
                // KNOWN BOUNDARY, recorded in ASSUMPTIONS.md: the check is
                // one-directional. A connector whose schema is a strict SUPERSET
                // of the registered one passes - protection between connectors
                // relies on each having at least one property the other lacks,
                // which holds for every connector in this repository.
                PushEngine.VerifySchemaOwnership("consultingwork", schema, null, log);
                PushEngine.VerifySchemaOwnership("consultingwork", schema, new Schema(), log);
            }
        }

        [Fact]
        public async Task The_ownership_check_actually_guards_the_schema_call_site()
        {
            // The pure-function tests above cannot notice the single call at
            // EnsureSchemaAsync being deleted. This drives the REAL call path:
            // a Ready connection carrying the tickets schema, approached by the
            // hierarchy engine, must throw before any write reaches the adapter.
            var adapter = new StubGraphAdapter(
                new Microsoft.Graph.Models.ExternalConnectors.ExternalConnection
                {
                    Id = "consultingwork",
                    State = Microsoft.Graph.Models.ExternalConnectors.ConnectionState.Ready,
                },
                new TicketsPushConnector().BuildSchema());

            var graph = new Microsoft.Graph.GraphServiceClient(adapter);

            var engine = new PushEngine(
                new HierarchyPushConnector(),
                TestData.ValidPushOptions(),
                graph,
                Logger.None,
                dryRun: false);

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.EnsureSchemaAsync());

            Assert.Contains("another connector", thrown.Message, StringComparison.Ordinal);
            Assert.Empty(adapter.Writes);
        }

        [Fact]
        public void An_empty_acl_fails_loudly_rather_than_writing_an_item_nobody_can_see()
        {
            // Graph accepts an item with no ACL and then returns it to no one, so
            // this has to be an exception rather than a warning.
            PushOptions options = TestData.ValidPushOptions();
            options.Acl = new AclOptions();

            Assert.Throws<InvalidOperationException>(() => PushEngine.BuildAcl(options));

            options.Acl = new AclOptions
            {
                GrantGroupObjectIds = new List<string> { " " + TestData.GroupObjectId + " " },
            };

            List<Acl> acl = PushEngine.BuildAcl(options);

            Acl single = Assert.Single(acl);
            Assert.Equal(AclType.Group, single.Type);
            Assert.Equal(AccessType.Grant, single.AccessType);
            Assert.Equal(TestData.GroupObjectId, single.Value);   // trimmed
        }

        [Fact]
        public void A_retry_after_header_is_honoured_in_preference_to_the_guess()
        {
            // Guessing low is what turns one 429 into a run of them.
            Assert.Equal(TimeSpan.FromSeconds(45), GraphThrottling.RetryAfter(ErrorWithHeader("Retry-After", "45")));

            // Header names are case insensitive on the wire.
            Assert.Equal(TimeSpan.FromSeconds(3), GraphThrottling.RetryAfter(ErrorWithHeader("retry-after", "3")));

            // A very long wait is capped rather than obeyed.
            Assert.Equal(
                TimeSpan.FromSeconds(GraphThrottling.MaxRetryAfterSeconds),
                GraphThrottling.RetryAfter(ErrorWithHeader("Retry-After", "99999")));

            // Absent, unparseable or nonsensical: fall back to the backoff.
            Assert.Null(GraphThrottling.RetryAfter(null));
            Assert.Null(GraphThrottling.RetryAfter(new Microsoft.Graph.Models.ODataErrors.ODataError()));
            Assert.Null(GraphThrottling.RetryAfter(ErrorWithHeader("Retry-After", "soon")));
            Assert.Null(GraphThrottling.RetryAfter(ErrorWithHeader("Retry-After", "0")));
            Assert.Null(GraphThrottling.RetryAfter(ErrorWithHeader("X-Other", "45")));
        }

        [Fact]
        public void The_backoff_grows_and_then_stops_growing()
        {
            Assert.Equal(TimeSpan.FromSeconds(4), GraphThrottling.Backoff(1));
            Assert.Equal(TimeSpan.FromSeconds(8), GraphThrottling.Backoff(2));
            Assert.Equal(TimeSpan.FromSeconds(60), GraphThrottling.Backoff(10));
        }

        [Fact]
        public void The_summary_counts_by_item_type_so_it_can_be_checked_against_the_source()
        {
            var summary = new PushSummary();

            foreach (string type in new[] { "Customer", "Engagement", "Engagement", "TimeEntry" })
            {
                summary.Count(type);
            }

            Assert.Equal(4, summary.Total);
            Assert.Equal(2, summary.ByType["Engagement"]);
            Assert.Equal("Customer=1, Engagement=2, TimeEntry=1", summary.Describe());
            Assert.Equal("none", new PushSummary().Describe());
        }

        [Fact]
        public void A_property_with_no_value_is_omitted_rather_than_sent_as_null()
        {
            // Graph rejects a null value rather than ignoring it, and in a
            // flattened hierarchy most columns are null on most rows: a customer
            // has no consultant and no hours.
            var item = new PushItem { Id = "cust1", ItemType = "Customer" };

            item.AddIfPresent("consultantName", (string)null);
            item.AddIfPresent("consultantEmail", string.Empty);
            item.AddIfPresent("hours", (double?)null);
            item.AddIfPresent("billable", (bool?)null);
            item.AddIfPresent("childCount", (long?)null);

            Assert.Empty(item.Properties);

            item.AddIfPresent("consultantName", "Priya Raman");
            item.AddIfPresent("hours", 7.5);
            item.AddIfPresent("billable", false);
            item.AddIfPresent("childCount", 5L);

            Assert.Equal(4, item.Properties.Count);
            Assert.Equal(false, item.Properties["billable"]);      // present, and false
            Assert.Equal(5L, item.Properties["childCount"]);
        }

        private static Microsoft.Graph.Models.ODataErrors.ODataError ErrorWithHeader(string name, string value)
        {
            return new Microsoft.Graph.Models.ODataErrors.ODataError
            {
                ResponseHeaders = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { name, new[] { value } },
                },
            };
        }
    }
}
