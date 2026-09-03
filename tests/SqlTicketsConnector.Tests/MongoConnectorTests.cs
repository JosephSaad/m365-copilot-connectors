// ---------------------------------------------------------------------------
// MongoConnectorTests.cs
// The MongoDB direct-push connector: its schema, its configuration rules and
// the per-document decisions in MongoDocumentMapper.
//
// The refusal worth reading twice is the encrypted field. CSFLE and Queryable
// Encryption store ciphertext, and ciphertext indexes without complaint: there
// is no error, no warning, and nothing downstream that can tell it from text.
// The failure is a connection full of noise that looks healthy on every
// dashboard. That is why the mapper throws rather than skipping the field, and
// why it is asserted here rather than left to a live test to discover.
//
// The collection-level refusals - a view rather than a collection, a missing
// collection - need a MongoDB to answer listCollections and are not exercised
// here.
// ---------------------------------------------------------------------------

#nullable enable

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using MongoDB.Bson;
    using MongoGraphPush;
    using PushCore;
    using Xunit;

    public class MongoConnectorTests
    {
        private static PushOptions Options(string collection = "records")
        {
            var options = new PushOptions();
            options.Source.ItemView = collection;
            options.DataSource.Server = "mongodb://mongo01.contoso.local/";
            options.DataSource.Database = "app";
            options.DataSource.ItemUrlTemplate = "https://records.contoso.com/record/{0}";
            return options;
        }

        private static BsonDocument Document(params (string Name, BsonValue Value)[] fields)
        {
            var document = new BsonDocument { { "_id", new ObjectId("6500000000000000000000aa") } };

            foreach ((string name, BsonValue value) in fields)
            {
                document[name] = value;
            }

            return document;
        }

        [Fact]
        public void The_schema_matches_the_relational_connectors()
        {
            string[] expected = { "recordId", "title", "status", "owner", "lastModified", "url" };

            Assert.Equal(
                expected,
                new MongoRecordsPushConnector().BuildSchema().Properties!.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void The_schema_carries_the_three_semantic_labels_Copilot_needs()
        {
            List<Label?> labels = new MongoRecordsPushConnector().BuildSchema().Properties!
                .SelectMany(p => p.Labels ?? new List<Label?>())
                .ToList();

            Assert.Contains(Label.Title, labels);
            Assert.Contains(Label.Url, labels);
            Assert.Contains(Label.LastModifiedDateTime, labels);
        }

        [Fact]
        public void A_missing_connection_uri_or_database_is_reported_against_its_own_key()
        {
            var errors = new ValidationErrors();
            PushOptions options = Options();
            options.DataSource.Server = string.Empty;
            options.DataSource.Database = string.Empty;

            new MongoRecordsPushConnector().Validate(options, errors);

            string text = errors.ToMessage();
            Assert.Contains("DataSource:Server", text, StringComparison.Ordinal);
            Assert.Contains("DataSource:Database", text, StringComparison.Ordinal);
        }

        [Fact]
        public void A_system_collection_is_refused()
        {
            var errors = new ValidationErrors();

            new MongoRecordsPushConnector().Validate(Options("system.users"), errors);

            Assert.Contains("Source:ItemView", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_ordinary_collection_validates()
        {
            var errors = new ValidationErrors();

            new MongoRecordsPushConnector().Validate(Options("records"), errors);

            Assert.DoesNotContain("Source:ItemView", errors.ToMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_default_collection_is_applied_when_configuration_omits_one()
        {
            PushOptions options = Options();
            options.Source.ItemView = string.Empty;

            new MongoRecordsPushConnector().ApplyDefaults(options);

            Assert.Equal("records", options.Source.ItemView);
        }

        [Fact]
        public void An_encrypted_field_stops_the_run_rather_than_indexing_ciphertext()
        {
            // The whole point of the class. See the file header.
            BsonDocument document = Document(
                ("title", "A record"),
                ("body", new BsonBinaryData(new byte[] { 1, 2, 3 }, BsonBinarySubType.Encrypted)));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => MongoDocumentMapper.Map(document, Options()));

            Assert.Contains("encrypted", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("body", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Ordinary_binary_data_is_not_mistaken_for_ciphertext()
        {
            // Only subtype 6 is encryption. A document carrying a thumbnail or a
            // UUID must not be refused as though it were.
            BsonDocument document = Document(
                ("title", "A record"),
                ("body", new BsonBinaryData(new byte[] { 1, 2, 3 }, BsonBinarySubType.Binary)));

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.NotNull(item);
        }

        [Fact]
        public void A_document_with_no_identifier_is_skipped()
        {
            var document = new BsonDocument { { "title", "No identifier" } };

            Assert.Null(MongoDocumentMapper.Map(document, Options()));
        }

        [Fact]
        public void A_null_identifier_is_skipped()
        {
            var document = new BsonDocument { { "_id", BsonNull.Value }, { "title", "t" } };

            Assert.Null(MongoDocumentMapper.Map(document, Options()));
        }

        [Fact]
        public void A_string_identifier_is_sanitised_to_what_Graph_accepts()
        {
            var document = new BsonDocument { { "_id", "rec/2026-09#01" }, { "title", "t" } };

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.NotNull(item);
            Assert.Equal("mongorecordrec20260901", item!.Id);

            // The unsanitised key is still what the property and the URL carry,
            // because that is what a person pastes back into the source.
            Assert.Equal("rec/2026-09#01", (string)item.Properties["recordId"]);
        }

        [Fact]
        public void An_identifier_that_sanitises_to_nothing_is_skipped_not_collapsed()
        {
            // "///" would otherwise compose the bare prefix, and every such
            // document would upsert onto one item.
            var document = new BsonDocument { { "_id", "///" }, { "title", "t" } };

            Assert.Null(MongoDocumentMapper.Map(document, Options()));
        }

        [Fact]
        public void A_long_identifier_is_truncated_inside_the_Graph_limit()
        {
            var document = new BsonDocument { { "_id", new string('a', 400) }, { "title", "t" } };

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.NotNull(item);
            Assert.True(item!.Id.Length <= 128, $"identifier was {item.Id.Length} characters");
            Assert.Equal(
                MongoDocumentMapper.IdPrefix.Length + MongoDocumentMapper.MaxKeyLength,
                item.Id.Length);
        }

        [Fact]
        public void updatedAt_is_preferred_for_the_modification_time()
        {
            BsonDocument document = Document(
                ("title", "t"),
                ("updatedAt", new BsonDateTime(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc))));

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.StartsWith("2026-09-01T08:00:00", (string)item!.Properties["lastModified"], StringComparison.Ordinal);
        }

        [Fact]
        public void Without_updatedAt_the_ObjectId_creation_time_is_used_and_is_not_a_modification_time()
        {
            // The fallback exists so the semantic label carries a value, not
            // because it is correct. A collection without updatedAt cannot be
            // read incrementally at all, and this asserts the fallback rather
            // than endorsing it.
            var id = new ObjectId("6500000000000000000000aa");
            BsonDocument document = Document(("title", "t"));

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.Equal(
                DateTime.SpecifyKind(id.CreationTime, DateTimeKind.Utc).ToString("o"),
                item!.Properties["lastModified"]);
        }

        [Fact]
        public void A_missing_text_field_maps_to_empty_rather_than_failing_the_document()
        {
            BsonDocument document = Document(("title", "Only a title"));

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.NotNull(item);
            Assert.Equal("Only a title", (string)item!.Properties["title"]);
            Assert.Equal(string.Empty, (string)item.Properties["status"]);
            Assert.Equal(string.Empty, item.Content);
        }

        [Fact]
        public void The_item_identifier_does_not_collide_with_the_relational_connectors()
        {
            BsonDocument document = Document(("title", "t"));

            PushItem? item = MongoDocumentMapper.Map(document, Options());

            Assert.StartsWith("mongorecord", item!.Id, StringComparison.Ordinal);
        }
    }
}
