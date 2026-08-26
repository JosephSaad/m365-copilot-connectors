// ---------------------------------------------------------------------------
// PushSchemaTests.cs
// The external schemas the connectors register, and the two platform rules that
// cannot be recovered from once a schema is live.
//
// This is the most expensive mistake in the system to make. A registered schema
// is append-only: a property can be added, but no property's type, annotation
// or label can ever be changed. Correcting one means deleting the connection
// and every item in it — 1126 items for the three level test case. These tests
// are what stands between an edit and that outcome.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Graph.Models.ExternalConnectors;
    using SqlGraphPush;
    using SqlHierarchyPush;
    using PushCore;
    using SqlTicketsConnector.Connector;
    using global::Connector.Security.Schema;
    using Xunit;
    using ContractProperty = Microsoft.Graph.Connectors.Contracts.Grpc.SourcePropertyDefinition;
    using ContractSchema = Microsoft.Graph.Connectors.Contracts.Grpc.DataSourceSchema;

    public class PushSchemaTests
    {
        /// <summary>
        /// The properties the three level views produce, in the order they are
        /// registered. Spelled out rather than derived, so that adding one to the
        /// schema is a deliberate two-file edit: the schema is append-only, and a
        /// property added by accident cannot be taken back out.
        /// </summary>
        private static readonly string[] HierarchyProperties =
        {
            "itemType", "title", "url", "lastModified", "containerName", "containerUrl", "hierarchyPath",
            "customerName", "customerCode", "accountManager", "industry", "region",
            "engagementName", "engagementCode", "projectManager", "practice", "status",
            "consultantName", "consultantEmail", "workType", "workDate", "hours", "billable",
            "contractValue", "totalHours", "childCount",
        };

        [Fact]
        public void The_hierarchy_schema_registers_exactly_the_properties_the_views_produce()
        {
            Schema schema = new HierarchyPushConnector().BuildSchema();

            Assert.Equal(HierarchyProperties, schema.Properties.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void No_property_in_any_schema_is_both_searchable_and_refinable()
        {
            foreach (Property property in AllProperties())
            {
                Assert.False(
                    property.IsSearchable == true && property.IsRefinable == true,
                    property.Name + " is both searchable and refinable. Microsoft Graph rejects that pair, " +
                    "and a schema cannot be corrected once registered.");
            }
        }

        [Fact]
        public void Every_property_name_in_any_schema_is_within_the_platform_limit()
        {
            foreach (Property property in AllProperties())
            {
                // Throws with the offending name if the rule is broken.
                ExternalSchemaRules.ValidatePropertyName(property.Name);
            }
        }

        [Fact]
        public void The_ancestor_fields_are_searchable_on_every_level_which_is_the_whole_requirement()
        {
            // A time entry item is only reachable by a search for its customer
            // because it physically carries the customer's text and that text is
            // searchable. Drop searchable from any of these and the three level
            // test case silently stops demonstrating the thing it exists for:
            // the push still succeeds and the cross-level search returns nothing.
            string[] mustBeSearchable =
            {
                "customerName", "customerCode", "accountManager",
                "engagementName", "engagementCode", "projectManager",
                "hierarchyPath", "containerName", "title", "consultantName",
            };

            Schema schema = new HierarchyPushConnector().BuildSchema();

            foreach (string name in mustBeSearchable)
            {
                Property property = schema.Properties.Single(p => p.Name == name);

                Assert.True(
                    property.IsSearchable,
                    name + " must be searchable: it is how a query at one level reaches items at another. " +
                    "See docs/HIERARCHY-TEST-CASE.md.");
            }
        }

        [Fact]
        public void Facet_fields_are_refinable_rather_than_searchable()
        {
            // The distinction the platform is drawing: searchable is what a person
            // types, refinable is what they filter by. These are filtered by.
            string[] mustBeRefinable = { "itemType", "industry", "region", "practice", "status", "workType" };

            Schema schema = new HierarchyPushConnector().BuildSchema();

            foreach (string name in mustBeRefinable)
            {
                Property property = schema.Properties.Single(p => p.Name == name);

                Assert.True(property.IsRefinable, name + " must be refinable to work as a facet.");
                Assert.False(property.IsSearchable, name + " cannot also be searchable.");
            }
        }

        [Fact]
        public void The_hierarchy_schema_carries_each_semantic_label_exactly_once()
        {
            // Semantic labels are what make an item eligible for the index Copilot
            // grounds on. Two properties claiming the same label is a registration
            // failure, and an absent Title or Url is an item that indexes but never
            // displays properly.
            var expected = new Dictionary<string, Label>
            {
                { "title", Label.Title },
                { "url", Label.Url },
                { "lastModified", Label.LastModifiedDateTime },
                { "containerName", Label.ContainerName },
                { "containerUrl", Label.ContainerUrl },
            };

            Schema schema = new HierarchyPushConnector().BuildSchema();

            foreach (KeyValuePair<string, Label> pair in expected)
            {
                Property property = schema.Properties.Single(p => p.Name == pair.Key);

                Assert.NotNull(property.Labels);
                Assert.Equal(new List<Label?> { pair.Value }, property.Labels);
            }

            List<Label?> allLabels = schema.Properties
                .Where(p => p.Labels is not null)
                .SelectMany(p => p.Labels)
                .ToList();

            Assert.Equal(expected.Count, allLabels.Count);
            Assert.Equal(allLabels.Count, allLabels.Distinct().Count());
        }

        [Fact]
        public void The_ticket_schema_is_unchanged_and_still_carries_its_title_and_url_labels()
        {
            Schema schema = new TicketsPushConnector().BuildSchema();

            Assert.Equal(
                new[] { "ticketId", "title", "status", "assignedTo", "lastModified", "url" },
                schema.Properties.Select(p => p.Name).ToArray());

            Assert.Equal("microsoft.graph.externalItem", schema.BaseType);

            Assert.Equal(
                new List<Label?> { Label.Title },
                schema.Properties.Single(p => p.Name == "title").Labels);
            Assert.Equal(
                new List<Label?> { Label.Url },
                schema.Properties.Single(p => p.Name == "url").Labels);
            Assert.True(schema.Properties.Single(p => p.Name == "status").IsRefinable);
        }

        [Fact]
        public void A_searchable_and_refinable_property_is_rejected_before_any_graph_call()
        {
            // Control evidence. Without this the failure arrives fifteen minutes
            // into server side registration, against a draft connection that then
            // has to be deleted rather than corrected.
            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => PushSchema.Prop("region", PropertyType.String, searchable: true, refinable: true));

            Assert.Contains("region", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("searchable and refinable", thrown.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Throws<InvalidOperationException>(
                () => ExternalSchemaRules.ValidateProperty("anything", searchable: true, refinable: true));
        }

        [Fact]
        public void A_property_name_the_platform_would_reject_is_caught_before_any_graph_call()
        {
            // Control evidence. 32 characters, alphanumeric, no exceptions.
            string overLimit = new string('a', ExternalSchemaRules.MaxPropertyNameLength + 1);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => PushSchema.Prop(overLimit, PropertyType.String, retrievable: true));

            Assert.Contains(overLimit, thrown.Message, StringComparison.Ordinal);

            // Non-ASCII letters pass char.IsLetterOrDigit but Graph rejects
            // them, which is why the validator is ASCII-only - and why these two
            // are in the list.
            foreach (string bad in new[]
                     { "customer_name", "customer-name", "customer name", "customer.name", "pr\u00e9nom", "customer\u00e9", string.Empty })
            {
                Assert.Throws<InvalidOperationException>(() => ExternalSchemaRules.ValidatePropertyName(bad));
            }

            // Exactly at the limit is allowed; the rule is a maximum, not a margin.
            ExternalSchemaRules.ValidatePropertyName(new string('a', ExternalSchemaRules.MaxPropertyNameLength));
        }

        [Fact]
        public void Item_ids_are_checked_against_the_same_alphanumeric_limit()
        {
            // The IDs sql/12-timesheet-views.sql composes, which is why they are
            // composed rather than taken from a natural key.
            foreach (string id in new[] { "cust12", "eng62", "time1052", "ticket7" })
            {
                Assert.True(ExternalSchemaRules.IsValidItemId(id));
                ExternalSchemaRules.ValidateItemId(id);
            }

            foreach (string id in new[] { "cust-12", "cust 12", "customer/12", "cust\u00e912", "", null })
            {
                Assert.False(ExternalSchemaRules.IsValidItemId(id));
                Assert.Throws<InvalidOperationException>(() => ExternalSchemaRules.ValidateItemId(id));
            }

            Assert.False(ExternalSchemaRules.IsValidItemId(new string('a', ExternalSchemaRules.MaxItemIdLength + 1)));
            Assert.True(ExternalSchemaRules.IsValidItemId(new string('a', ExternalSchemaRules.MaxItemIdLength)));
        }

        [Fact]
        public void The_agent_hosted_schema_obeys_the_same_rules_it_will_be_mapped_onto()
        {
            // The connector hands a DataSourceSchema to the agent, which maps it
            // onto a Graph schema — so the Graph rules reach it too, one step
            // removed. It builds its properties by hand rather than through
            // PushSchema, so this is the check that it stays inside them.
            ContractSchema schema = SqlDataSource.BuildSchema();

            uint searchable = (uint)ContractProperty.Types.SearchAnnotations.IsSearchable;
            uint refinable = (uint)ContractProperty.Types.SearchAnnotations.IsRefinable;

            foreach (ContractProperty property in schema.PropertyList)
            {
                ExternalSchemaRules.ValidatePropertyName(property.Name);

                Assert.False(
                    (property.DefaultSearchAnnotations & searchable) == searchable &&
                    (property.DefaultSearchAnnotations & refinable) == refinable,
                    property.Name + " is both searchable and refinable.");
            }
        }

        private static IEnumerable<Property> AllProperties()
        {
            return new HierarchyPushConnector().BuildSchema().Properties
                .Concat(new TicketsPushConnector().BuildSchema().Properties);
        }
    }
}
