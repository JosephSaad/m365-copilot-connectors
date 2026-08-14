// ---------------------------------------------------------------------------
// SqlDataSource.cs
// Queries and schema for dbo.Tickets. All SQL text lives here so a DBA can read
// exactly what the connector runs without opening the rest of the solution.
//
// Every query is parameterised and ordered by (LastModified, TicketId), which is
// the ordering the composite checkpoint depends on.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using Microsoft.Graph.Connectors.Contracts.Grpc;

    /// <summary>
    /// Query text and the property list handed to the agent.
    /// </summary>
    public static class SqlDataSource
    {
        /// <summary>Parameter name for the watermark instant.</summary>
        public const string WatermarkTimeParameter = "@watermarkTime";

        /// <summary>Parameter name for the watermark tie breaker.</summary>
        public const string WatermarkIdParameter = "@watermarkId";

        private const string WatermarkPredicate =
            "(LastModified > @watermarkTime OR (LastModified = @watermarkTime AND TicketId > @watermarkId))";

        private const string OrderBy = " ORDER BY LastModified, TicketId;";

        /// <summary>
        /// Live rows from the watermark onward. Soft deleted rows are excluded:
        /// the agent removes from the index anything a full crawl no longer returns.
        /// </summary>
        public static string FullCrawlQuery(bool softDeleteEnabled)
        {
            return "SELECT " + Columns(softDeleteEnabled) +
                   " FROM dbo.Tickets WHERE " +
                   (softDeleteEnabled ? "IsDeleted = 0 AND " : string.Empty) +
                   WatermarkPredicate +
                   OrderBy;
        }

        /// <summary>
        /// Rows changed since the watermark, including soft deleted ones so the
        /// incremental crawl can emit a DeletedItem for them.
        /// </summary>
        public static string IncrementalCrawlQuery(bool softDeleteEnabled)
        {
            return "SELECT " + Columns(softDeleteEnabled) +
                   " FROM dbo.Tickets WHERE " +
                   WatermarkPredicate +
                   OrderBy;
        }

        /// <summary>
        /// Cheap probe used by ValidateAuthentication. Selecting IsDeleted here
        /// makes a missing soft delete column a connection wizard error rather
        /// than a first crawl failure.
        /// </summary>
        public static string ValidationQuery(bool softDeleteEnabled)
        {
            return "SELECT TOP 1 " + (softDeleteEnabled ? "TicketId, IsDeleted" : "TicketId") + " FROM dbo.Tickets;";
        }

        /// <summary>
        /// Property list handed to the agent, which converts it into the Microsoft
        /// Graph schema. Nothing here calls Graph directly.
        /// </summary>
        public static DataSourceSchema BuildSchema()
        {
            var schema = new DataSourceSchema();

            schema.PropertyList.Add(StringProperty(
                "ticketId",
                "Ticket ID",
                SourcePropertyDefinition.Types.SearchAnnotations.IsQueryable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable));

            var title = StringProperty(
                "title",
                "Title",
                SourcePropertyDefinition.Types.SearchAnnotations.IsSearchable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsQueryable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable);
            title.DefaultSemanticLabels.Add(SourcePropertyDefinition.Types.SearchPropertyLabel.Title);
            schema.PropertyList.Add(title);

            schema.PropertyList.Add(StringProperty(
                "status",
                "Status",
                SourcePropertyDefinition.Types.SearchAnnotations.IsQueryable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsRefinable));

            schema.PropertyList.Add(StringProperty(
                "assignedTo",
                "Assigned to",
                SourcePropertyDefinition.Types.SearchAnnotations.IsQueryable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable));

            // Exactly one property carries IsContent. Title and Url semantic labels
            // plus a content property are what make items eligible for the semantic
            // index Copilot grounds on.
            schema.PropertyList.Add(StringProperty(
                "body",
                "Body",
                SourcePropertyDefinition.Types.SearchAnnotations.IsSearchable |
                SourcePropertyDefinition.Types.SearchAnnotations.IsContent));

            var modified = new SourcePropertyDefinition
            {
                Name = "lastModified",
                Label = "Last modified",
                Type = SourcePropertyDefinition.Types.SourcePropertyType.DateTime,
                DefaultSearchAnnotations = (uint)(
                    SourcePropertyDefinition.Types.SearchAnnotations.IsQueryable |
                    SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable),
            };
            modified.DefaultSemanticLabels.Add(
                SourcePropertyDefinition.Types.SearchPropertyLabel.LastModifiedDateTime);
            schema.PropertyList.Add(modified);

            var url = StringProperty(
                "url",
                "Link",
                SourcePropertyDefinition.Types.SearchAnnotations.IsRetrievable);
            url.DefaultSemanticLabels.Add(SourcePropertyDefinition.Types.SearchPropertyLabel.Url);
            schema.PropertyList.Add(url);

            return schema;
        }

        /// <summary>
        /// Returns the name of the property the connection schema flags as content,
        /// or null when the schema is absent or unflagged.
        /// </summary>
        public static string ResolveContentPropertyName(DataSourceSchema schema)
        {
            if (schema == null)
            {
                return null;
            }

            uint isContent = (uint)SourcePropertyDefinition.Types.SearchAnnotations.IsContent;

            foreach (var property in schema.PropertyList)
            {
                if ((property.DefaultSearchAnnotations & isContent) == isContent ||
                    (property.RequiredSearchAnnotations & isContent) == isContent)
                {
                    return property.Name;
                }
            }

            return null;
        }

        private static string Columns(bool softDeleteEnabled)
        {
            return softDeleteEnabled
                ? "TicketId, Title, Status, AssignedTo, Body, LastModified, IsDeleted"
                : "TicketId, Title, Status, AssignedTo, Body, LastModified";
        }

        private static SourcePropertyDefinition StringProperty(
            string name,
            string label,
            SourcePropertyDefinition.Types.SearchAnnotations annotations)
        {
            return new SourcePropertyDefinition
            {
                Name = name,
                Label = label,
                Type = SourcePropertyDefinition.Types.SourcePropertyType.String,
                DefaultSearchAnnotations = (uint)annotations,
            };
        }
    }
}
