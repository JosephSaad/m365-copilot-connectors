// ---------------------------------------------------------------------------
// PushConfigurationTests.cs
// Startup validation for the shared push configuration.
//
// Two of these guards are not style checks. The neighbour guard stops one
// connector being pointed at another's connection within an executable, and the
// engine's schema-ownership check (PushEngineTests) covers the cross-executable
// case without any connector naming another. The view name guard is what makes
// concatenating that name into a query safe, since a table cannot be a
// parameter.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.IO;
    using SqlGraphPush;
    using SqlHierarchyPush;
    using SqlPushCore;
    using SqlConnector.Security.Configuration;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushConfigurationTests
    {
        [Fact]
        public void Valid_configuration_produces_no_errors_for_either_connector()
        {
            ValidationErrors hierarchy = TestData.ValidPushOptions().Validate();
            Assert.False(hierarchy.HasErrors, hierarchy.ToMessage());

            ValidationErrors tickets = TestData.ValidPushOptions("sqltickets", "dbo.Tickets").Validate();
            Assert.False(tickets.HasErrors, tickets.ToMessage());
        }

        [Fact]
        public void A_connector_cannot_be_pointed_at_a_neighbours_connection_without_naming_it()
        {
            // The generic form of the rule above, and the one that covers a
            // connector added later: the host compares the configured ID against
            // every other connector hosted alongside it, so nothing has to be
            // told about the newcomer.
            var connectors = new IPushConnector[] { new TicketsPushConnector(), new HierarchyPushConnector() };
            PushOptions options = TestData.ValidPushOptions("sqltickets");
            var errors = new ValidationErrors();

            PushHost.RejectNeighboursConnection(options, new HierarchyPushConnector(), connectors, errors);

            Assert.True(errors.HasErrors);
            Assert.Contains(errors.Errors, e => e.Contains("'tickets' connector", StringComparison.Ordinal));

            // Its own connection is fine, obviously.
            var own = new ValidationErrors();
            PushHost.RejectNeighboursConnection(
                TestData.ValidPushOptions("consultingwork"), new HierarchyPushConnector(), connectors, own);
            Assert.False(own.HasErrors, own.ToMessage());
        }

        [Theory]
        [InlineData("ab")]                                   // shorter than three
        [InlineData("sql_tickets")]                          // underscore is not alphanumeric
        [InlineData("consulting work")]                      // space
        [InlineData("fa\u00e7ade01")]                        // non-ASCII letter; Graph rejects it
        [InlineData("MicrosoftWork")]                        // reserved prefix
        [InlineData("None")]                                 // reserved value
        public void A_connection_id_graph_would_reject_is_caught_in_configuration(string connectionId)
        {
            PushOptions options = TestData.ValidPushOptions(connectionId);

            Assert.True(options.Validate().HasErrors, connectionId + " should have been rejected");
        }

        [Fact]
        public void A_connection_id_of_exactly_thirty_two_characters_is_accepted()
        {
            // The rule is 3 to 32 inclusive. Both ends belong to the caller.
            Assert.False(TestData.ValidPushOptions(new string('a', 32)).Validate().HasErrors);
            Assert.True(TestData.ValidPushOptions(new string('a', 33)).Validate().HasErrors);
        }

        [Theory]
        [InlineData("dbo.vwExternalItems; DROP TABLE dbo.Customers")]
        [InlineData("dbo.vwExternalItems WHERE 1=1")]
        [InlineData("[dbo].[vwExternalItems]")]
        [InlineData("dbo.vw-External-Items")]
        [InlineData("dbo..vwExternalItems")]
        [InlineData("server.dbo.vwExternalItems")]
        [InlineData("dbo.2026Snapshot")]
        [InlineData("123")]
        public void A_view_name_that_is_not_a_plain_identifier_is_rejected(string view)
        {
            // Control evidence. The view name is concatenated into the query
            // because a view cannot be a parameter; restricting it to an
            // identifier shape is the entire reason that is safe.
            ValidationErrors errors = TestData.ValidPushOptions("consultingwork", view).Validate();

            Assert.True(errors.HasErrors, view + " should have been rejected");
            Assert.Contains(errors.Errors, e => e.StartsWith("Source:ItemView:", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("vwExternalItems")]
        [InlineData("dbo.vwExternalItems")]
        [InlineData("reporting.vw_external_items")]
        public void A_view_name_may_be_bare_or_schema_qualified(string view)
        {
            ValidationErrors errors = TestData.ValidPushOptions("consultingwork", view).Validate();

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void Every_invalid_field_is_reported_in_one_pass()
        {
            var options = new PushOptions
            {
                Environment = "Prod",                        // not one of the allowed values
                Auth = new AuthOptions
                {
                    Mode = "Password",                       // not a supported mode
                    TenantId = "8f3a1c22-0d5e-4a1e-9c2b-6a7d5e4f3b21",
                    ClientId = string.Empty,
                },
                DataSource = new DataSourceOptions
                {
                    Server = string.Empty,
                    Database = string.Empty,
                    SqlAuthMode = "WindowsIntegrated",
                },
                Acl = new AclOptions(),                      // empty: no silent everyone
                Graph = new GraphSection
                {
                    ConnectionId = "sql tickets",
                    ConnectionName = string.Empty,
                    SchemaReadyTimeoutMinutes = 0,           // below the allowed range
                },
                Source = new SourceSection
                {
                    ItemView = "dbo.vwExternalItems; DROP TABLE dbo.Customers",
                    MaxItems = -1,
                },
            };

            ValidationErrors errors = options.Validate();

            Assert.True(errors.HasErrors);

            // One run should be enough to learn about all of them.
            string[] expected =
            {
                "Environment",
                "Auth:Mode",
                "Auth:ClientId",
                "DataSource:Server",
                "DataSource:Database",
                "Acl:GrantGroupObjectIds",
                "Graph:ConnectionId",
                "Graph:ConnectionName",
                "Graph:SchemaReadyTimeoutMinutes",
                "Source:ItemView",
                "Source:MaxItems",
            };

            foreach (string path in expected)
            {
                Assert.Contains(errors.Errors, e => e.StartsWith(path + ":", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void A_configuration_file_that_omits_a_section_falls_back_to_the_connectors_own_defaults()
        {
            // This is what keeps an already deployed appsettings.json working when
            // the core gains a section. SqlGraphPush shipped without Source for
            // three releases; the connector declares dbo.Tickets and the file that
            // never mentioned it still validates.
            var options = new PushOptions();

            PushHost.ApplyDefaults(options, new TicketsPushConnector());

            Assert.Equal("sqltickets", options.Graph.ConnectionId);
            Assert.Equal("SQL Support Tickets", options.Graph.ConnectionName);
            Assert.Equal("dbo.Tickets", options.Source.ItemView);

            // What the file does say wins over the default.
            var configured = new PushOptions
            {
                Graph = new GraphSection { ConnectionId = "othertickets" },
                Source = new SourceSection { ItemView = "dbo.OtherTickets" },
            };

            PushHost.ApplyDefaults(configured, new TicketsPushConnector());

            Assert.Equal("othertickets", configured.Graph.ConnectionId);
            Assert.Equal("dbo.OtherTickets", configured.Source.ItemView);
        }

        [Fact]
        public void Connector_specific_settings_live_in_a_bag_so_the_core_does_not_grow_a_property()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                "{ \"Settings\": { \"RegionFilter\": \"EMEA\", \"BatchSize\": \"250\", \"IncludeDrafts\": \"true\" } }");

            try
            {
                PushOptions options = PushOptions.Load(path);

                Assert.Equal("EMEA", options.Setting("RegionFilter"));
                Assert.Equal(250, options.Setting("BatchSize", 25));
                Assert.True(options.Setting("IncludeDrafts", false));

                // Case insensitive, and the comparer survives deserialization —
                // which it does not by default, because System.Text.Json builds a
                // fresh dictionary rather than filling the one the property held.
                Assert.Equal("EMEA", options.Setting("regionfilter"));

                // Absent keys fall back rather than throwing.
                Assert.Equal("all", options.Setting("Practice", "all"));
                Assert.Equal(7, options.Setting("Missing", 7));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_missing_configuration_file_names_the_path_it_looked_in()
        {
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => PushOptions.Load(missing));

            Assert.Contains(missing, thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_key_specific_file_is_preferred_and_the_shared_one_is_the_fallback()
        {
            // How two connectors coexist in one executable without either one's
            // configuration being touched by the other.
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                string shared = Path.Combine(directory, "appsettings.json");
                File.WriteAllText(shared, "{}");

                Assert.Equal(shared, PushOptions.ResolveFile(directory, "tickets"));

                string specific = Path.Combine(directory, "appsettings.tickets.json");
                File.WriteAllText(specific, "{}");

                Assert.Equal(specific, PushOptions.ResolveFile(directory, "tickets"));

                // The neighbour still gets the shared file. Adding one connector's
                // configuration does not move another's.
                Assert.Equal(shared, PushOptions.ResolveFile(directory, "consultingwork"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Malformed_json_is_reported_as_configuration_rather_than_an_unhandled_crash()
        {
            // Exit code 2, not a stack trace: the operator edited the file and
            // needs to be told which file and roughly where.
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{ \"Environment\": \"Production\", }}");

            try
            {
                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => PushOptions.Load(path));

                Assert.Contains(path, thrown.Message, StringComparison.Ordinal);
                Assert.Contains("not valid JSON", thrown.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Comments_and_trailing_commas_are_accepted_because_the_shipped_files_have_them()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                "{\n  // which view the flattened items come from\n" +
                "  \"Source\": { \"ItemView\": \"dbo.vwExternalItems\", },\n}");

            try
            {
                PushOptions options = PushOptions.Load(path);

                Assert.Equal("dbo.vwExternalItems", options.Source.ItemView);
                Assert.Equal(path, options.SourcePath);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
