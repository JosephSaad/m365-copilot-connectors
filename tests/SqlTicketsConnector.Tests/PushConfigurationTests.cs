// ---------------------------------------------------------------------------
// PushConfigurationTests.cs
// Startup validation for the two direct push tools.
//
// Two of these guards are not style checks. The connection ID guard is what
// stops the three level tool from being pointed at the ticket test case's
// connection, where it would try to register a second, incompatible schema onto
// a connection whose schema is already fixed. The view name guard is what makes
// concatenating that name into a query safe, since a view cannot be a parameter.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.IO;
    using SqlGraphPush;
    using SqlHierarchyPush;
    using SqlTicketsConnector.Security.Configuration;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushConfigurationTests
    {
        [Fact]
        public void Valid_configuration_produces_no_errors_for_either_push_tool()
        {
            ValidationErrors hierarchy = TestData.ValidHierarchyOptions().Validate();
            Assert.False(hierarchy.HasErrors, hierarchy.ToMessage());

            ValidationErrors tickets = TestData.ValidPushOptions().Validate();
            Assert.False(tickets.HasErrors, tickets.ToMessage());
        }

        [Fact]
        public void The_hierarchy_tool_refuses_the_ticket_test_cases_connection_id()
        {
            // Control evidence. Sharing an ID means one tool silently cannot
            // manage the connection the other created — OwnedBy — and the second
            // schema cannot be registered over the first, which is already fixed.
            HierarchyOptions options = TestData.ValidHierarchyOptions();
            options.Graph.ConnectionId = "sqltickets";

            ValidationErrors errors = options.Validate();

            Assert.True(errors.HasErrors);
            Assert.Contains(errors.Errors, e => e.Contains("Graph:ConnectionId", StringComparison.Ordinal));
            Assert.Contains(errors.Errors, e => e.Contains("ticket test case", StringComparison.Ordinal));

            // Case is not a defence: connection IDs are matched case insensitively.
            options.Graph.ConnectionId = "SqlTickets";
            Assert.True(options.Validate().HasErrors);
        }

        [Theory]
        [InlineData("ab")]                                   // shorter than three
        [InlineData("sql_tickets")]                          // underscore is not alphanumeric
        [InlineData("consulting work")]                      // space
        [InlineData("MicrosoftWork")]                        // reserved prefix
        [InlineData("None")]                                 // reserved value
        public void A_connection_id_graph_would_reject_is_caught_in_configuration(string connectionId)
        {
            HierarchyOptions hierarchy = TestData.ValidHierarchyOptions();
            hierarchy.Graph.ConnectionId = connectionId;
            Assert.True(hierarchy.Validate().HasErrors, connectionId + " should have been rejected");

            PushOptions tickets = TestData.ValidPushOptions();
            tickets.Graph.ConnectionId = connectionId;
            Assert.True(tickets.Validate().HasErrors, connectionId + " should have been rejected");
        }

        [Fact]
        public void A_connection_id_of_exactly_thirty_two_characters_is_accepted()
        {
            // The rule is 3 to 32 inclusive. Both ends belong to the caller.
            HierarchyOptions options = TestData.ValidHierarchyOptions();
            options.Graph.ConnectionId = new string('a', 32);
            Assert.False(options.Validate().HasErrors);

            options.Graph.ConnectionId = new string('a', 33);
            Assert.True(options.Validate().HasErrors);
        }

        [Theory]
        [InlineData("dbo.vwExternalItems; DROP TABLE dbo.Customers")]
        [InlineData("dbo.vwExternalItems WHERE 1=1")]
        [InlineData("[dbo].[vwExternalItems]")]
        [InlineData("dbo.vw-External-Items")]
        [InlineData("dbo..vwExternalItems")]
        [InlineData("server.dbo.vwExternalItems")]
        public void A_view_name_that_is_not_a_plain_identifier_is_rejected(string view)
        {
            // Control evidence. The view name is concatenated into the query
            // because a view cannot be a parameter; restricting it to an
            // identifier shape is the entire reason that is safe.
            HierarchyOptions options = TestData.ValidHierarchyOptions();
            options.Source.ItemView = view;

            ValidationErrors errors = options.Validate();

            Assert.True(errors.HasErrors, view + " should have been rejected");
            Assert.Contains(errors.Errors, e => e.Contains("Source:ItemView", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("vwExternalItems")]
        [InlineData("dbo.vwExternalItems")]
        [InlineData("reporting.vw_external_items")]
        public void A_view_name_may_be_bare_or_schema_qualified(string view)
        {
            HierarchyOptions options = TestData.ValidHierarchyOptions();
            options.Source.ItemView = view;

            ValidationErrors errors = options.Validate();

            Assert.False(errors.HasErrors, errors.ToMessage());
        }

        [Fact]
        public void Every_invalid_field_in_a_hierarchy_configuration_is_reported_in_one_pass()
        {
            var options = new HierarchyOptions
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
                Graph = new HierarchyGraphSection
                {
                    ConnectionId = "sqltickets",             // the other test case's
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
        public void A_missing_configuration_file_names_the_path_it_looked_in()
        {
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

            InvalidOperationException hierarchy = Assert.Throws<InvalidOperationException>(
                () => HierarchyOptions.Load(missing));
            Assert.Contains(missing, hierarchy.Message, StringComparison.Ordinal);

            InvalidOperationException tickets = Assert.Throws<InvalidOperationException>(
                () => PushOptions.Load(missing));
            Assert.Contains(missing, tickets.Message, StringComparison.Ordinal);
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
                    () => HierarchyOptions.Load(path));

                Assert.Contains(path, thrown.Message, StringComparison.Ordinal);
                Assert.Contains("not valid JSON", thrown.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Comments_and_trailing_commas_are_accepted_because_the_shipped_file_has_them()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                "{\n  // which view the flattened items come from\n" +
                "  \"Source\": { \"ItemView\": \"dbo.vwExternalItems\", },\n}");

            try
            {
                HierarchyOptions options = HierarchyOptions.Load(path);

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
