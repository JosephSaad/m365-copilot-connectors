// ---------------------------------------------------------------------------
// HierarchySchema.cs
// The 26 property external schema for Customer -> Engagement -> TimeEntry.
//
// It lives in its own class rather than inside Program.cs's top level statements
// for one reason: a registered schema cannot be corrected. Getting it wrong
// costs the connection and all 1126 items, so the shape has to be assertable by
// a test that runs on every build, and nothing inside top level statements is
// reachable from a test assembly.
//
// One flat schema serves all three levels. A time entry leaves the engagement
// and customer columns populated; a customer leaves the descendant columns
// unset. That is what "flat" costs, and it is cheaper than three connections,
// which could not be searched as one thing.
//
// Two platform rules shape every line below, both enforced by Prop:
//   * isSearchable and isRefinable are mutually exclusive. Anything a person
//     types goes in the searchable column; anything they filter or facet by
//     goes in the refinable one.
//   * property names are 32 alphanumeric characters at most.
// ---------------------------------------------------------------------------

namespace SqlHierarchyPush;

using Microsoft.Graph.Models.ExternalConnectors;
using SqlTicketsConnector.Security.Schema;

/// <summary>The external schema registered by this tool.</summary>
public static class HierarchySchema
{
    /// <summary>Builds the schema, validating every property as it goes.</summary>
    /// <returns>The schema to PATCH onto the connection.</returns>
    public static Schema Build()
    {
        return new Schema
        {
            BaseType = "microsoft.graph.externalItem",
            Properties = new List<Property>
            {
                // --- which level this item is, and where it sits ----------------
                // Refinable, not searchable: you facet by it, you do not type it.
                Prop("itemType", PropertyType.String, queryable: true, retrievable: true, refinable: true),

                Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                    label: Label.Title),
                Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
                Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                    label: Label.LastModifiedDateTime),

                // containerName and containerUrl are how the platform expresses
                // "this item sits inside that one" — an engagement's container is
                // its customer, a time entry's is its engagement. It is the closest
                // a flat index gets to the hierarchy, and result surfaces show it.
                Prop("containerName", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                    label: Label.ContainerName),
                Prop("containerUrl", PropertyType.String, retrievable: true, label: Label.ContainerUrl),

                // The breadcrumb as one searchable string: "Contoso > Data Platform
                // Migration > 2026-08-14 Priya Raman". Matches a query that names
                // two levels at once, which neither level alone would.
                Prop("hierarchyPath", PropertyType.String, searchable: true, queryable: true, retrievable: true),

                // --- level 1, present on ALL THREE levels -----------------------
                // This block is the requirement. customerName is searchable on the
                // time entry as well as on the customer, which is the only reason a
                // search for the customer reaches the time entry at all.
                Prop("customerName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("customerCode", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("accountManager", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("industry", PropertyType.String, queryable: true, retrievable: true, refinable: true),
                Prop("region", PropertyType.String, queryable: true, retrievable: true, refinable: true),

                // --- level 2, present on engagements and time entries -----------
                Prop("engagementName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("engagementCode", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("projectManager", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("practice", PropertyType.String, queryable: true, retrievable: true, refinable: true),
                Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

                // --- level 3 ----------------------------------------------------
                Prop("consultantName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
                Prop("consultantEmail", PropertyType.String, queryable: true, retrievable: true),
                Prop("workType", PropertyType.String, queryable: true, retrievable: true, refinable: true),
                Prop("workDate", PropertyType.DateTime, queryable: true, retrievable: true),
                Prop("hours", PropertyType.Double, queryable: true, retrievable: true),
                Prop("billable", PropertyType.Boolean, queryable: true, retrievable: true),

                // --- roll ups, so an answer can cite a number without arithmetic -
                Prop("contractValue", PropertyType.Double, queryable: true, retrievable: true),
                Prop("totalHours", PropertyType.Double, queryable: true, retrievable: true),
                Prop("childCount", PropertyType.Int64, queryable: true, retrievable: true),
            },
        };
    }

    /// <summary>
    /// Builds one property, rejecting the two combinations Graph will not accept.
    ///
    /// Catching them here turns a schema registration failure fifteen minutes
    /// into the wait — with a connection left in draft that cannot be corrected
    /// without deleting it — into an exception before the first Graph call.
    /// </summary>
    /// <param name="name">Property name, 32 alphanumeric characters at most.</param>
    /// <param name="type">The Graph property type.</param>
    /// <param name="searchable">Full text searchable. Mutually exclusive with refinable.</param>
    /// <param name="queryable">Usable in a KQL restriction.</param>
    /// <param name="retrievable">Returned in results.</param>
    /// <param name="refinable">Usable as a refiner. Mutually exclusive with searchable.</param>
    /// <param name="label">Optional semantic label.</param>
    /// <returns>The validated property.</returns>
    public static Property Prop(
        string name,
        PropertyType type,
        bool searchable = false,
        bool queryable = false,
        bool retrievable = false,
        bool refinable = false,
        Label? label = null)
    {
        ExternalSchemaRules.ValidateProperty(name, searchable, refinable);

        var property = new Property
        {
            Name = name,
            Type = type,
            IsSearchable = searchable,
            IsQueryable = queryable,
            IsRetrievable = retrievable,
            IsRefinable = refinable,
        };

        if (label is not null)
        {
            property.Labels = new List<Label?> { label };
        }

        return property;
    }
}
