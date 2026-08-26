// ---------------------------------------------------------------------------
// CrawlItemBuilder.cs
// Turns a row into a ContentItem: property values, ACL and content, with the
// size cap enforced before anything is written to the stream.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Google.Protobuf.WellKnownTypes;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog;
    using SqlTicketsConnector.Logging;
    using Connector.Security.Configuration;
    using Connector.Security.Content;

    /// <summary>An item ready to stream, or a decision to skip the row.</summary>
    public sealed class BuiltItem
    {
        /// <summary>Gets or sets the item ID.</summary>
        public string ItemId { get; set; }

        /// <summary>Gets or sets the composed item, or null when the row must be skipped.</summary>
        public ContentItem ContentItem { get; set; }

        /// <summary>Gets or sets the size of the emitted content in bytes.</summary>
        public int ContentBytes { get; set; }

        /// <summary>Gets or sets a value indicating whether content was truncated.</summary>
        public bool Truncated { get; set; }

        /// <summary>Gets or sets a value indicating whether the item is too large to emit at all.</summary>
        public bool Oversize { get; set; }
    }

    /// <summary>Composes crawl items from ticket rows.</summary>
    public sealed class CrawlItemBuilder
    {
        // Headroom for the CrawlItem envelope around the ContentItem: item ID,
        // item type tag and protobuf framing.
        private const int EnvelopeMarginBytes = 4096;

        private readonly AccessControlList aclTemplate;
        private readonly int maxContentBytes;
        private readonly string itemUrlTemplate;
        private readonly ILogger logger;

        /// <summary>Initializes the builder.</summary>
        public CrawlItemBuilder(
            IReadOnlyList<string> aclGroupObjectIds,
            int maxContentBytes,
            string itemUrlTemplate,
            ILogger logger)
        {
            // Built once, cloned per item: an empty ACL configuration is a startup
            // failure, not a per-item surprise.
            this.aclTemplate = AclBuilder.Build(aclGroupObjectIds);
            this.maxContentBytes = maxContentBytes;
            // No fallback URL: startup validation requires the template, so an
            // empty one reaching this constructor is a caller bug - and a silent
            // sample-URL substitution would send every search result to a page
            // that does not exist.
            if (string.IsNullOrWhiteSpace(itemUrlTemplate))
            {
                throw new ArgumentException(
                    "itemUrlTemplate is required. DataSource:ItemUrlTemplate is validated at startup; " +
                    "reaching this point without one is a bug in the caller.",
                    nameof(itemUrlTemplate));
            }

            this.itemUrlTemplate = itemUrlTemplate;
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>Composes the item for a row, applying the content cap.</summary>
        public BuiltItem Build(TicketRow row, DataSourceSchema connectionSchema, CrawlMetrics metrics)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            var result = new BuiltItem { ItemId = row.ItemId };

            string contentValue = this.SelectContent(row, connectionSchema);
            TruncationResult truncation = ContentTruncator.Truncate(contentValue, this.maxContentBytes);

            if (truncation.Truncated)
            {
                // Item ID and sizes only. The content itself is customer data.
                this.logger.Warning(
                    "Item {ItemId} content truncated from {OriginalBytes} to {FinalBytes} bytes by the " +
                    "DataSource:MaxContentBytes cap of {MaxContentBytes}.",
                    row.ItemId,
                    truncation.OriginalBytes,
                    truncation.FinalBytes,
                    this.maxContentBytes);

                if (metrics != null)
                {
                    metrics.RecordTruncated();
                }
            }

            ContentItem item = this.Compose(row, truncation.Content);
            int serializedSize = item.CalculateSize();
            int limit = DataSourceOptions.PlatformItemLimitBytes - EnvelopeMarginBytes;

            if (serializedSize > limit)
            {
                // The property values alone pushed the item over the platform cap.
                // Trim the content by the overflow and try once more.
                int budget = truncation.FinalBytes - (serializedSize - limit);

                if (budget <= 0)
                {
                    this.logger.Warning(
                        "Item {ItemId} is {SerializedBytes} bytes before content and exceeds the {Limit} byte " +
                        "platform cap. The row is skipped.",
                        row.ItemId,
                        serializedSize,
                        DataSourceOptions.PlatformItemLimitBytes);

                    result.Oversize = true;
                    return result;
                }

                TruncationResult second = ContentTruncator.Truncate(truncation.Content, budget);
                item = this.Compose(row, second.Content);
                truncation = new TruncationResult(second.Content, truncation.OriginalBytes, second.FinalBytes, true);

                this.logger.Warning(
                    "Item {ItemId} content trimmed again to {FinalBytes} bytes to fit the platform item cap.",
                    row.ItemId,
                    second.FinalBytes);

                if (item.CalculateSize() > DataSourceOptions.PlatformItemLimitBytes)
                {
                    result.Oversize = true;
                    return result;
                }
            }

            result.ContentItem = item;
            result.ContentBytes = truncation.FinalBytes;
            result.Truncated = truncation.Truncated;
            return result;
        }

        private ContentItem Compose(TicketRow row, string contentValue)
        {
            var propertyValues = new SourcePropertyValueMap();

            foreach (KeyValuePair<string, GenericType> pair in this.PropertyValues(row))
            {
                propertyValues.Values.Add(pair.Key, pair.Value);
            }

            return new ContentItem
            {
                PropertyValues = propertyValues,
                Content = new Content
                {
                    ContentType = Content.Types.ContentType.Text,
                    ContentValue = contentValue ?? string.Empty,
                },
                AccessList = this.aclTemplate.Clone(),
            };
        }

        private Dictionary<string, GenericType> PropertyValues(TicketRow row)
        {
            return new Dictionary<string, GenericType>(StringComparer.Ordinal)
            {
                { "ticketId", new GenericType { StringValue = row.TicketId.ToString(CultureInfo.InvariantCulture) } },
                { "title", new GenericType { StringValue = row.Title ?? string.Empty } },
                { "status", new GenericType { StringValue = row.Status ?? string.Empty } },
                { "assignedTo", new GenericType { StringValue = row.AssignedTo ?? string.Empty } },
                { "body", new GenericType { StringValue = row.Body ?? string.Empty } },
                {
                    "url",
                    new GenericType
                    {
                        StringValue = string.Format(CultureInfo.InvariantCulture, this.itemUrlTemplate, row.TicketId),
                    }
                },
                {
                    "lastModified",
                    new GenericType
                    {
                        DateTimeValue = Timestamp.FromDateTime(
                            DateTime.SpecifyKind(row.LastModifiedUtc, DateTimeKind.Utc)),
                    }
                },
            };
        }

        private string SelectContent(TicketRow row, DataSourceSchema connectionSchema)
        {
            string contentPropertyName = SqlDataSource.ResolveContentPropertyName(connectionSchema);

            if (contentPropertyName == null)
            {
                return row.Body ?? string.Empty;
            }

            GenericType selected;
            if (!this.PropertyValues(row).TryGetValue(contentPropertyName, out selected))
            {
                return row.Body ?? string.Empty;
            }

            // A oneof getter returns an empty string, not null, when the stored case
            // is not stringValue. Fall back rather than index an empty document.
            string value = selected.StringValue;
            return string.IsNullOrEmpty(value) ? row.Body ?? string.Empty : value;
        }
    }
}
