// ---------------------------------------------------------------------------
// RedactionDestructuringPolicy.cs
// Serilog destructuring policy: decides what a logged object is allowed to
// become in a log event.
//
// Treat this as a security control, not a formatting nicety. The rows in
// dbo.Tickets carry customer data, and a log file is not an approved store for
// it, so any protobuf message, ticket row or content item collapses to a
// summary carrying an identifier and a size.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Logging
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using Google.Protobuf;
    using Microsoft.Data.SqlClient;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog.Core;
    using Serilog.Events;
    using SqlTicketsConnector.Connector;

    /// <summary>
    /// Replaces sensitive objects with safe summaries when they are destructured
    /// into a log event.
    /// </summary>
    public sealed class RedactionDestructuringPolicy : IDestructuringPolicy
    {
        /// <summary>
        /// Types that must never be rendered through ToString().
        ///
        /// Serilog stringifies an object at capture time when it is logged through a
        /// plain {Value} hole, which happens before any destructuring policy runs.
        /// Registering these as scalars keeps the object itself in the event, where
        /// <see cref="Security.Logging.ScrubbingEnricher"/> hands it back to this
        /// policy. Both spellings of a log call therefore end up redacted.
        /// </summary>
        public static readonly Type[] NeverStringify =
        {
            typeof(SqlConnectionStringBuilder),
            typeof(X509Certificate2),
            typeof(TicketRow),
            typeof(CrawlItem),
            typeof(IncrementalCrawlItem),
            typeof(ContentItem),
            typeof(Content),
            typeof(SourcePropertyValueMap),
            typeof(GenericType),
            typeof(AccessControlList),
            typeof(AuthenticationData),
            typeof(BasicCredential),
            typeof(OAuth2ClientCredential),
            typeof(OAuth2ClientCredentialResponse),
            typeof(WindowsCredential),
            typeof(GetCrawlStreamRequest),
            typeof(GetIncrementalCrawlStreamRequest),
            typeof(ValidateAuthenticationRequest),
            typeof(CrawlStreamBit),
            typeof(IncrementalCrawlStreamBit),
        };

        /// <inheritdoc />
        public bool TryDestructure(
            object value,
            ILogEventPropertyValueFactory propertyValueFactory,
            out LogEventPropertyValue result)
        {
            var connectionStringBuilder = value as SqlConnectionStringBuilder;
            if (connectionStringBuilder != null)
            {
                // Server and database only. Never the credential portion.
                result = Structure(
                    "SqlConnection",
                    Field("Server", connectionStringBuilder.DataSource),
                    Field("Database", connectionStringBuilder.InitialCatalog));
                return true;
            }

            var certificate = value as X509Certificate2;
            if (certificate != null)
            {
                // Thumbprint and subject are identifiers, not secrets.
                result = Structure(
                    "Certificate",
                    Field("Thumbprint", certificate.Thumbprint),
                    Field("Subject", certificate.Subject),
                    Field("NotAfter", certificate.NotAfter.ToUniversalTime().ToString("o")));
                return true;
            }

            var row = value as TicketRow;
            if (row != null)
            {
                result = Structure(
                    "TicketRow",
                    Field("TicketId", row.TicketId),
                    Field("LastModifiedUtc", row.LastModifiedUtc.ToString("o")),
                    Field("IsDeleted", row.IsDeleted),
                    Field("ContentBytes", Encoding.UTF8.GetByteCount(row.Body ?? string.Empty)),
                    Field("ValuesRedacted", true));
                return true;
            }

            var crawlItem = value as CrawlItem;
            if (crawlItem != null)
            {
                result = Structure(
                    "CrawlItem",
                    Field("ItemId", crawlItem.ItemId),
                    Field("ItemType", crawlItem.ItemType.ToString()),
                    Field("ValuesRedacted", true));
                return true;
            }

            var incrementalItem = value as IncrementalCrawlItem;
            if (incrementalItem != null)
            {
                result = Structure(
                    "IncrementalCrawlItem",
                    Field("ItemId", incrementalItem.ItemId),
                    Field("ItemType", incrementalItem.ItemType.ToString()),
                    Field("ValuesRedacted", true));
                return true;
            }

            var contentItem = value as ContentItem;
            if (contentItem != null)
            {
                result = Structure(
                    "ContentItem",
                    Field("PropertyCount", contentItem.PropertyValues == null ? 0 : contentItem.PropertyValues.Values.Count),
                    Field(
                        "ContentBytes",
                        contentItem.Content == null
                            ? 0
                            : Encoding.UTF8.GetByteCount(contentItem.Content.ContentValue ?? string.Empty)),
                    Field("AclEntryCount", contentItem.AccessList == null ? 0 : contentItem.AccessList.Entries.Count),
                    Field("ValuesRedacted", true));
                return true;
            }

            // Anything else generated from the Microsoft contracts renders as JSON
            // through ToString(), which would include property values and admin
            // supplied credentials. Collapse it to the message name.
            var message = value as IMessage;
            if (message != null)
            {
                result = new ScalarValue("[" + message.Descriptor.Name + " redacted]");
                return true;
            }

            result = null;
            return false;
        }

        private static LogEventProperty Field(string name, object value)
        {
            return new LogEventProperty(name, new ScalarValue(value));
        }

        private static StructureValue Structure(string typeTag, params LogEventProperty[] properties)
        {
            return new StructureValue(new List<LogEventProperty>(properties), typeTag);
        }
    }
}
