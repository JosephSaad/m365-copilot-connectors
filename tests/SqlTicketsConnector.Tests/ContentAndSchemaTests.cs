// ---------------------------------------------------------------------------
// ContentAndSchemaTests.cs
// Item size cap, ACL shape and the schema handed to the agent.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog.Core;
    using SqlTicketsConnector.Connector;
    using global::Connector.Security.Content;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class ContentAndSchemaTests
    {
        [Fact]
        public void Oversize_content_is_truncated_to_the_cap_rather_than_emitted_whole()
        {
            const int Cap = 4096;
            string body = new string('a', 20000);

            TruncationResult result = ContentTruncator.Truncate(body, Cap);

            Assert.True(result.Truncated);
            Assert.Equal(20000, result.OriginalBytes);
            Assert.True(result.FinalBytes <= Cap, "truncated content must fit inside the cap");
            Assert.Contains("truncated", result.Content, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Content_inside_the_cap_is_untouched()
        {
            TruncationResult result = ContentTruncator.Truncate("short body", 4096);

            Assert.False(result.Truncated);
            Assert.Equal("short body", result.Content);
        }

        [Fact]
        public void Truncation_never_splits_a_multi_byte_character()
        {
            // Four byte characters straddling the cut point.
            string body = string.Concat(Enumerable.Repeat("\U0001F600", 500));

            TruncationResult result = ContentTruncator.Truncate(body, 300);

            Assert.True(result.FinalBytes <= 300);

            byte[] encoded = Encoding.UTF8.GetBytes(result.Content);
            string round = Encoding.UTF8.GetString(encoded);

            Assert.Equal(result.Content, round);
            Assert.DoesNotContain('�', result.Content);
        }

        [Fact]
        public void A_built_item_carries_truncated_content_and_the_configured_acl()
        {
            var builder = new CrawlItemBuilder(
                new[] { TestData.GroupObjectId },
                2048,
                "https://tickets.contoso.com/ticket/{0}",
                Logger.None);

            TicketRow row = TestData.Row(42, DateTime.UtcNow, "big");
            row.Body = new string('b', 10000);

            var metrics = new SqlTicketsConnector.Logging.CrawlMetrics();
            BuiltItem built = builder.Build(row, SqlDataSource.BuildSchema(), metrics);

            Assert.False(built.Oversize);
            Assert.True(built.Truncated);
            Assert.True(built.ContentBytes <= 2048);
            Assert.Equal(1, metrics.ItemsTruncated);
            Assert.Equal("ticket42", built.ItemId);

            AccessControlEntry entry = Assert.Single(built.ContentItem.AccessList.Entries);
            Assert.Equal(AccessControlEntry.Types.AclAccessType.Grant, entry.AccessType);
            Assert.Equal(Principal.Types.PrincipalType.Group, entry.Principal.Type);
            Assert.Equal(Principal.Types.IdentityType.AadId, entry.Principal.IdentityType);
            Assert.Equal(Principal.Types.IdentitySource.AzureActiveDirectory, entry.Principal.IdentitySource);
            Assert.Equal(TestData.GroupObjectId, entry.Principal.Value);
        }

        [Fact]
        public void An_empty_acl_configuration_fails_loudly_instead_of_granting_everyone()
        {
            Assert.Throws<InvalidOperationException>(() => AclBuilder.Build(new string[0]));
            Assert.Throws<InvalidOperationException>(() => AclBuilder.Build(null));
        }

        [Fact]
        public void The_schema_carries_title_and_url_labels_and_exactly_one_content_property()
        {
            DataSourceSchema schema = SqlDataSource.BuildSchema();

            SourcePropertyDefinition title = schema.PropertyList.Single(p => p.Name == "title");
            Assert.Contains(SourcePropertyDefinition.Types.SearchPropertyLabel.Title, title.DefaultSemanticLabels);

            SourcePropertyDefinition url = schema.PropertyList.Single(p => p.Name == "url");
            Assert.Contains(SourcePropertyDefinition.Types.SearchPropertyLabel.Url, url.DefaultSemanticLabels);

            uint isContent = (uint)SourcePropertyDefinition.Types.SearchAnnotations.IsContent;

            SourcePropertyDefinition content = Assert.Single(
                schema.PropertyList,
                p => (p.DefaultSearchAnnotations & isContent) == isContent);

            Assert.Equal("body", content.Name);
            Assert.Equal("body", SqlDataSource.ResolveContentPropertyName(schema));
        }

        [Fact]
        public void The_crawl_queries_compare_the_composite_watermark_and_respect_soft_deletes()
        {
            string full = SqlDataSource.FullCrawlQuery(true);
            string incremental = SqlDataSource.IncrementalCrawlQuery(true);

            foreach (string query in new[] { full, incremental })
            {
                Assert.Contains("LastModified > @watermarkTime", query, StringComparison.Ordinal);
                Assert.Contains(
                    "LastModified = @watermarkTime AND TicketId > @watermarkId",
                    query,
                    StringComparison.Ordinal);
                Assert.Contains("ORDER BY LastModified, TicketId", query, StringComparison.Ordinal);
            }

            // A full crawl hides soft deleted rows; the incremental crawl needs them
            // so it can report the delete.
            Assert.Contains("IsDeleted = 0", full, StringComparison.Ordinal);
            Assert.DoesNotContain("IsDeleted = 0", incremental, StringComparison.Ordinal);
            Assert.DoesNotContain("IsDeleted", SqlDataSource.FullCrawlQuery(false), StringComparison.Ordinal);
        }
    }
}
