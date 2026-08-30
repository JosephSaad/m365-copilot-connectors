// ---------------------------------------------------------------------------
// BatchRetrySerializationTests.cs
// Serializing one ExternalItem twice must not lose its ACL.
//
// THE DEFECT THIS PINS. Graph SDK models are backed models: a backing store
// marks its values clean once written, so a second serialization of the same
// instance emits only what changed since - which, for an object nobody touched,
// is nothing. Graph answers 400 NullOrEmptyValue, "'Acl' is null or empty", and
// refuses the item terminally.
//
// A retry is exactly that second serialization. GraphBatchWriter keeps one
// ExternalItem per tracked item and re-sends it, so any throttled item was
// being retried without its grants.
//
// WHY IT SURVIVED SO LONG. Until the first run that was ever throttled, no item
// in this project's history had been serialized twice - throttleWaits was zero
// on every run. That run took 429s on 191 items and every one of those 191 came
// back 400 on the retry, a one-for-one correspondence with nothing else in the
// log to explain it. The run reported success.
//
// AND WHY NO EARLIER TEST CAUGHT IT. StubGraphAdapter hands back a plain
// JsonSerializationWriterFactory. A real GraphServiceClient wraps its factory
// through ApiClientBuilder.EnableBackingStoreForSerializationWriterFactory, and
// that wrapper is the whole mechanism - without it a model serializes in full
// every time and the bug cannot appear. Earlier attempts to reproduce the
// sibling defect (one ACL shared across items, fixed in 44e464f) failed for
// this reason and were honestly recorded as unreproducible. This file supplies
// the missing wrapper, which is what makes the failure appear on demand.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.Graph.Models.ExternalConnectors;
    using Microsoft.Kiota.Abstractions;
    using Microsoft.Kiota.Abstractions.Serialization;
    using Microsoft.Kiota.Abstractions.Store;
    using Microsoft.Kiota.Serialization.Json;
    using Xunit;

    public class BatchRetrySerializationTests
    {
        [Fact]
        public void An_item_serialized_twice_still_carries_its_acl()
        {
            ExternalItem item = Item();
            ISerializationWriterFactory factory = BackingStoreEnabledFactory();

            string first = Serialize(factory, item);
            Assert.Contains("\"value\":\"group-object-id\"", first);

            // The retry. Without the reset in GraphBatchWriter.NextChunk this is
            // where the ACL disappears and Graph answers 400 NullOrEmptyValue.
            ResetForSerialization(item);
            string second = Serialize(factory, item);

            Assert.Contains("\"acl\"", second);
            Assert.Contains("\"value\":\"group-object-id\"", second);
        }

        [Fact]
        public void Without_the_reset_the_second_serialization_loses_the_acl()
        {
            // The defect itself, pinned so that this file proves the fix bites
            // rather than merely passing. If a future SDK stops dropping the
            // ACL, this test fails and the reset can be reconsidered on
            // evidence - which is the only honest reason to remove it.
            ExternalItem item = Item();
            ISerializationWriterFactory factory = BackingStoreEnabledFactory();

            string first = Serialize(factory, item);
            string second = Serialize(factory, item);   // no reset

            Assert.Contains("group-object-id", first);
            Assert.DoesNotContain("group-object-id", second);
        }

        /// <summary>The reset GraphBatchWriter applies before every serialization.</summary>
        private static void ResetForSerialization(ExternalItem item)
        {
            MarkDirty(item);
            MarkDirty(item.Content);
            MarkDirty(item.Properties);

            if (item.Acl is not null)
            {
                foreach (Acl acl in item.Acl)
                {
                    MarkDirty(acl);
                }
            }
        }

        private static void MarkDirty(object? model)
        {
            if (model is IBackedModel { BackingStore: not null } backed)
            {
                backed.BackingStore.InitializationCompleted = false;
            }
        }

        /// <summary>What GraphServiceClient builds, and what the stub adapter does not.</summary>
        private static ISerializationWriterFactory BackingStoreEnabledFactory()
        {
            var registry = new SerializationWriterFactoryRegistry();
            registry.ContentTypeAssociatedFactories["application/json"] = new JsonSerializationWriterFactory();

            return ApiClientBuilder.EnableBackingStoreForSerializationWriterFactory(registry);
        }

        private static string Serialize(ISerializationWriterFactory factory, ExternalItem item)
        {
            ISerializationWriter writer = factory.GetSerializationWriter("application/json");
            writer.WriteObjectValue(string.Empty, item);

            using Stream stream = writer.GetSerializedContent();
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }

        private static ExternalItem Item() => new()
        {
            Id = "cust18003",
            Acl = new List<Acl>
            {
                new Acl { Type = AclType.Group, Value = "group-object-id", AccessType = AccessType.Grant },
            },
            Properties = new Properties
            {
                AdditionalData = new Dictionary<string, object> { ["title"] = "Contoso" },
            },
            Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "body" },
        };
    }
}
