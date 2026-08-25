// ---------------------------------------------------------------------------
// TicketSchema.cs
// The six property external schema for dbo.Tickets.
//
// Lifted out of Program.cs's top level statements so a test can assert it. A
// registered schema cannot be corrected — only added to — so the cost of a
// wrong annotation is deleting the connection and every item in it, and that
// deserves a check that runs on every build rather than a careful read.
//
// The properties went through Prop when this moved, which is a change in
// behaviour rather than a move: previously they were object initialisers with
// no guard at all, so a searchable-and-refinable pair added here would have
// failed server side, fifteen minutes into registration, against a draft
// connection that then had to be deleted. Now it throws before the first call.
// ---------------------------------------------------------------------------

namespace SqlGraphPush;

using Microsoft.Graph.Models.ExternalConnectors;
using SqlTicketsConnector.Security.Schema;

/// <summary>The external schema registered by this tool.</summary>
public static class TicketSchema
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
                Prop("ticketId", PropertyType.String, queryable: true, retrievable: true),

                // Title and Url semantic labels plus a content payload are what
                // make items eligible for the semantic index that Copilot reads.
                Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                    label: Label.Title),

                // Refinable rather than searchable: a status is filtered by, not
                // typed. The two annotations are mutually exclusive.
                Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

                Prop("assignedTo", PropertyType.String, queryable: true, retrievable: true),
                Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                    label: Label.LastModifiedDateTime),
                Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
            },
        };
    }

    /// <summary>
    /// Builds one property, rejecting the two combinations Graph will not accept.
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
