// ---------------------------------------------------------------------------
// MongoRecordsPushConnector.cs
// A MongoDB collection, pushed straight to
// /external/connections/{id}/items/{itemId}.
//
// Path B: MongoDB has no query-and-row shape, so this connector creates its own
// IPushSource rather than sitting on PushCore.Db. Everything else is still the
// engine's - schema registration, ACLs, $batch, throttling, change detection,
// the delete sweep, checkpointing, redaction and exit codes.
//
// The document contract is fixed and documented rather than inferred, for the
// reason ROUTING-DECISIONS section 7 gives: Graph indexes DECLARED properties,
// an inferred schema changes the moment a document does, and a schema change is
// a connection-level operation rather than a per-item one. A collection that
// does not carry these fields is mapped into them by a view or a projection on
// the source side, not guessed at here.
// ---------------------------------------------------------------------------

namespace MongoGraphPush;

using Connector.Security.Configuration;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;

/// <summary>Records from a MongoDB collection, one item per document.</summary>
public sealed class MongoRecordsPushConnector : IPushConnector
{
    /// <summary>The vault key holding the database password, when one is used.</summary>
    public const string PasswordKey = "MongoPassword";

    /// <inheritdoc/>
    public string Key => "mongodb";

    /// <inheritdoc/>
    public string DisplayName => "Records from a MongoDB collection";

    /// <inheritdoc/>
    public string DefaultConnectionId => "mongorecords";

    /// <inheritdoc/>
    public string DefaultConnectionName => "MongoDB Records";

    /// <inheritdoc/>
    public string DefaultDescription => "Records ingested from MongoDB";

    /// <summary>
    /// The same six properties the SQL, Oracle and Teradata connectors publish.
    /// </summary>
    /// <returns>The schema.</returns>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            PushSchema.Prop("recordId", PropertyType.String, queryable: true, retrievable: true),

            PushSchema.Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.Title),

            PushSchema.Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("owner", PropertyType.String, queryable: true, retrievable: true),

            PushSchema.Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                label: Label.LastModifiedDateTime),


            // The label the engine derives from the source's classifications,
            // when a Sensitivity mapping is configured. Registered
            // UNCONDITIONALLY and written only when the mapping is on, for the
            // reason AtlasCatalogueConnector gives: a registered schema is
            // append-only, so a property added after a connection reaches Ready
            // cannot be PATCHed in - the alternative is deleting the connection
            // and every item in it the day somebody wants the mapping.
            //
            // String, not StringCollection: one item has ONE label. Several
            // classifications collapse to the most restrictive, which is why the
            // mapping is an ordered list.
            PushSchema.Prop(
                SensitivityOptions.DefaultProperty,
                PropertyType.String,
                queryable: true,
                retrievable: true,
                refinable: true),

            PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url));
    }

    /// <inheritdoc/>
    public IPushSource CreateSource(PushSourceContext context)
    {
        return new MongoPushSource(context);
    }

    /// <inheritdoc/>
    public void ApplyDefaults(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            options.Source.ItemView = "records";
        }
    }

    /// <inheritdoc/>
    public void Validate(PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        // Not DbSourceRules and not SqlSourceRules: neither describes this
        // source. A Mongo deployment has a connection URI and a database, no
        // SqlAuthMode, and its "view" is a collection name whose legal
        // characters are not a SQL identifier's.
        if (string.IsNullOrWhiteSpace(options.DataSource.Server))
        {
            errors.Add("DataSource:Server", "is required: a mongodb:// or mongodb+srv:// connection URI.");
        }

        if (string.IsNullOrWhiteSpace(options.DataSource.Database))
        {
            errors.Add("DataSource:Database", "is required: the database holding the collection.");
        }

        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            errors.Add("Source:ItemView", "is required: the collection to read.");
        }
        else if (options.Source.ItemView.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Source:ItemView", "must not be a system collection.");
        }

        this.ValidateOptions(options, errors);
    }

    /// <inheritdoc/>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        UrlTemplateValidator.Validate(errors, "DataSource:ItemUrlTemplate", options.DataSource.ItemUrlTemplate);
    }
}
