// ---------------------------------------------------------------------------
// HierarchyPushConnector.cs
// The three level test case: Customer -> Engagement -> TimeEntry.
//
// This is the whole connector. Credentials, the vault, the SQL connection,
// connection and schema registration, truncation, ACLs, throttling, exit codes
// and logging are the engine's, in SqlPushCore, identical for every source.
//
// WHAT THIS DEMONSTRATES
//
// A Graph external item has a flat property list. There is no parent property,
// no child collection and no join at retrieval time. Copilot fetches individual
// items; it does not traverse anything. So a hierarchy cannot be indexed as a
// hierarchy - it has to be flattened, with every descendant physically carrying
// its ancestors' text, or a search for the customer will never reach the time
// entries.
//
// That flattening lives in sql/12-timesheet-views.sql, deliberately: a DBA can
// read exactly what leaves the database, and this file holds one query against
// one view with no join logic at all.
// ---------------------------------------------------------------------------

namespace SqlHierarchyPush;

using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using SqlPushCore;
using SqlConnector.Security.Configuration;

/// <summary>Customers, engagements and logged time, flattened by SQL views.</summary>
public sealed class HierarchyPushConnector : IPushConnector
{
    /// <summary>The ticket test case's connection, which this must never be pointed at.</summary>
    private const string TicketConnectionId = "sqltickets";

    /// <inheritdoc/>
    public string Key => "consultingwork";

    /// <inheritdoc/>
    public string DisplayName => "Customers, engagements and logged time";

    /// <inheritdoc/>
    public string DefaultConnectionId => "consultingwork";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Consulting work";

    /// <inheritdoc/>
    public string DefaultDescription => "Customers, engagements and logged time";

    /// <inheritdoc/>
    public string DefaultItemView => "dbo.vwExternalItems";

    /// <summary>
    /// One flat schema serves all three levels. A time entry leaves the
    /// engagement and customer columns populated; a customer leaves the
    /// descendant columns unset. That is what "flat" costs, and it is cheaper
    /// than three connections, which could not be searched as one thing.
    ///
    /// Two platform rules shape every line, both enforced by PushSchema.Prop:
    ///   * isSearchable and isRefinable are mutually exclusive. Anything a
    ///     person types goes in the searchable column; anything they filter or
    ///     facet by goes in the refinable one.
    ///   * property names are 32 alphanumeric characters at most.
    /// </summary>
    /// <returns>The 26 property schema.</returns>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            // --- which level this item is, and where it sits --------------------
            // Refinable, not searchable: you facet by it, you do not type it.
            PushSchema.Prop("itemType", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            PushSchema.Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.Title),
            PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
            PushSchema.Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                label: Label.LastModifiedDateTime),

            // containerName and containerUrl are how the platform expresses
            // "this item sits inside that one" - an engagement's container is its
            // customer, a time entry's is its engagement. It is the closest a
            // flat index gets to the hierarchy, and result surfaces show it.
            PushSchema.Prop("containerName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true, label: Label.ContainerName),
            PushSchema.Prop("containerUrl", PropertyType.String, retrievable: true, label: Label.ContainerUrl),

            // The breadcrumb as one searchable string: "Contoso > Data Platform
            // Migration > 2026-08-14 Priya Raman". Matches a query that names two
            // levels at once, which neither level alone would.
            PushSchema.Prop("hierarchyPath", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),

            // --- level 1, present on ALL THREE levels ---------------------------
            // This block is the requirement. customerName is searchable on the
            // time entry as well as on the customer, which is the only reason a
            // search for the customer reaches the time entry at all.
            PushSchema.Prop("customerName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("customerCode", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("accountManager", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("industry", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("region", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 2, present on engagements and time entries ---------------
            PushSchema.Prop("engagementName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("engagementCode", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("projectManager", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("practice", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 3 --------------------------------------------------------
            PushSchema.Prop("consultantName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("consultantEmail", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("workType", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("workDate", PropertyType.DateTime, queryable: true, retrievable: true),
            PushSchema.Prop("hours", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("billable", PropertyType.Boolean, queryable: true, retrievable: true),

            // --- roll ups, so an answer can cite a number without arithmetic ----
            PushSchema.Prop("contractValue", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("totalHours", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("childCount", PropertyType.Int64, queryable: true, retrievable: true));
    }

    /// <summary>
    /// Parents first. A run interrupted halfway then leaves customers and
    /// engagements present with some time entries missing, which is a coherent
    /// index; the reverse would leave orphaned children whose ancestors are not
    /// there to be found.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The query against the flattening view.</returns>
    public string BuildQuery(PushOptions options)
    {
        // The view name is validated as an identifier in SourceSection.Validate,
        // which is what makes concatenating it safe.
        string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

        return
            $"SELECT {top}ItemId, ItemType, Title, Url, LastModified, HierarchyPath, ContainerName, ContainerUrl, " +
            "CustomerId, CustomerName, CustomerCode, Industry, Region, AccountManager, " +
            "EngagementId, EngagementName, EngagementCode, Practice, Status, ProjectManager, " +
            "ConsultantName, ConsultantEmail, WorkDate, Hours, Billable, WorkType, " +
            "ContractValue, TotalHours, ChildCount, Content " +
            $"FROM {options.Source.ItemView} " +
            "ORDER BY CASE ItemType WHEN 'Customer' THEN 0 WHEN 'Engagement' THEN 1 ELSE 2 END, ItemId;";
    }

    /// <inheritdoc/>
    public PushItem? MapRow(SqlDataReader reader, PushOptions options)
    {
        var item = new PushItem
        {
            Id = SqlRead.Text(reader, "ItemId"),
            ItemType = SqlRead.Text(reader, "ItemType"),
            Content = SqlRead.Text(reader, "Content"),
        };

        // Null properties are omitted rather than sent as null: a customer has no
        // consultant, and Graph rejects a null value rather than ignoring it.
        item.Properties["itemType"] = item.ItemType;
        item.Properties["title"] = SqlRead.Text(reader, "Title");
        item.Properties["url"] = SqlRead.Text(reader, "Url");
        item.Properties["lastModified"] = SqlRead.Utc(reader, "LastModified");
        item.Properties["hierarchyPath"] = SqlRead.Text(reader, "HierarchyPath");
        item.Properties["customerName"] = SqlRead.Text(reader, "CustomerName");
        item.Properties["customerCode"] = SqlRead.Text(reader, "CustomerCode");
        item.Properties["accountManager"] = SqlRead.Text(reader, "AccountManager");
        item.Properties["industry"] = SqlRead.Text(reader, "Industry");
        item.Properties["region"] = SqlRead.Text(reader, "Region");

        item.AddIfPresent("containerName", SqlRead.Text(reader, "ContainerName"));
        item.AddIfPresent("containerUrl", SqlRead.Text(reader, "ContainerUrl"));
        item.AddIfPresent("engagementName", SqlRead.Text(reader, "EngagementName"));
        item.AddIfPresent("engagementCode", SqlRead.Text(reader, "EngagementCode"));
        item.AddIfPresent("projectManager", SqlRead.Text(reader, "ProjectManager"));
        item.AddIfPresent("practice", SqlRead.Text(reader, "Practice"));
        item.AddIfPresent("status", SqlRead.Text(reader, "Status"));
        item.AddIfPresent("consultantName", SqlRead.Text(reader, "ConsultantName"));
        item.AddIfPresent("consultantEmail", SqlRead.Text(reader, "ConsultantEmail"));
        item.AddIfPresent("workType", SqlRead.Text(reader, "WorkType"));
        item.AddIfPresent("workDate", SqlRead.NullableUtc(reader, "WorkDate"));
        item.AddIfPresent("hours", SqlRead.Number(reader, "Hours"));
        item.AddIfPresent("billable", SqlRead.Flag(reader, "Billable"));
        item.AddIfPresent("contractValue", SqlRead.Number(reader, "ContractValue"));
        item.AddIfPresent("totalHours", SqlRead.Number(reader, "TotalHours"));

        double? childCount = SqlRead.Number(reader, "ChildCount");

        if (childCount.HasValue)
        {
            if (childCount.Value != Math.Floor(childCount.Value))
            {
                // The shipped view CASTs this to INT; a repointed view might not,
                // and a silent truncation would put a wrong count in the index.
                throw new InvalidOperationException(
                    $"ChildCount for item {item.Id} is not a whole number. The view must produce an integer.");
            }

            item.AddIfPresent("childCount", (long?)childCount.Value);
        }

        return item;
    }

    /// <summary>
    /// Reserves the ticket test case's connection ID.
    ///
    /// The host already refuses a connection belonging to another connector in
    /// the same executable, which covers anything added here later. It cannot
    /// see across executables, and SqlGraphPush is a separate one, so that
    /// single ID is named explicitly.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator.</param>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        if (string.Equals(options.Graph.ConnectionId, TicketConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Graph:ConnectionId",
                "is the ticket test case's connection. The two test cases register different schemas and a " +
                "registered schema cannot be changed, so they must not share a connection ID.");
        }
    }
}
