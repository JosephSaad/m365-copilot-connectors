// ---------------------------------------------------------------------------
// PushSchema.cs
// The property builder every connector's BuildSchema uses.
//
// It exists so the two rules that cannot be recovered from are applied by
// construction rather than by remembering. A registered schema is append-only:
// no property's type, annotation or label can be changed afterwards, so a
// mistake is corrected only by deleting the connection and every item in it.
// Catching it here turns a failure fifteen minutes into a server side
// registration, against a draft connection nobody can fix, into an exception
// before the first Graph call.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using Microsoft.Graph.Models.ExternalConnectors;
using SqlConnector.Security.Schema;

/// <summary>Builds schema properties, refusing the ones Graph will not accept.</summary>
public static class PushSchema
{
    /// <summary>Wraps a validated property list in a schema.</summary>
    /// <param name="properties">The properties, normally built with <see cref="Prop"/>.</param>
    /// <returns>The schema to register.</returns>
    public static Schema Of(params Property[] properties)
    {
        return new Schema
        {
            BaseType = "microsoft.graph.externalItem",
            Properties = new List<Property>(properties),
        };
    }

    /// <summary>Builds one property, rejecting the combinations Graph will not accept.</summary>
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
