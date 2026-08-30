// ---------------------------------------------------------------------------
// AtlasCatalogueConnector.cs
// The data catalogue as a searchable thing.
//
// The other two CDP connectors index what is IN the lake. This one indexes what
// the lake CONTAINS - which database holds which table, who owns it, what it is
// tagged with, and what produced it. That is a different question and often a
// more valuable one, because the answer today is usually "ask around".
//
// It is also the only one of the three that can describe data it may not index.
// A table Ranger row-filters or masks never reaches the index as rows, but its
// existence, its columns and its owner are things the people granted select
// already see - so the catalogue entry is indexed for exactly them. The tables
// hardest to index are frequently the ones most worth cataloguing.
//
// See AtlasPushSource for the whole of the access reasoning, including why this
// connector is deliberately stricter than Atlas itself.
// ---------------------------------------------------------------------------

namespace CdpGraphPush;

using CdpConnector.Source;
using CdpConnector.Source.Acl;
using CdpConnector.Source.Atlas;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using Connector.Security.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;

/// <summary>The Apache Atlas catalogue, one external item per described entity.</summary>
public sealed class AtlasCatalogueConnector : IPushConnector
{
    /// <inheritdoc/>
    public string Key => "cdpatlascatalog";

    /// <inheritdoc/>
    public string DisplayName => "CDP Atlas catalogue";

    /// <inheritdoc/>
    public string DefaultConnectionId => "cdpatlascatalog";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Cloudera data catalogue";

    /// <inheritdoc/>
    public string DefaultDescription =>
        "Databases, tables and paths on the Cloudera CDP cluster: owners, descriptions, classifications and lineage";

    /// <summary>
    /// Gets a value indicating that every entry carries its own grants.
    ///
    /// They come from Ranger's policies on the described table rather than from
    /// a list in a configuration file, so a policy change reaches the catalogue
    /// on the next run without anybody editing appsettings.
    /// </summary>
    public bool ItemsCarryTheirOwnAcl => true;

    /// <inheritdoc/>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            PushSchema.Prop("title", PropertyType.String, searchable: true, retrievable: true, label: Label.Title),
            PushSchema.Prop("qualifiedName", PropertyType.String, searchable: true, retrievable: true),

            // Searchable, not refinable: somebody hunting for the table holding
            // an address types the column name, and that is a query rather than
            // a filter.
            PushSchema.Prop("columnNames", PropertyType.String, searchable: true, retrievable: true),
            PushSchema.Prop("description", PropertyType.String, searchable: true, retrievable: true),

            PushSchema.Prop("entityKind", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("entityType", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("databaseName", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("clusterName", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("ownerName", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // The two fields that make a catalogue worth having: what a dataset
            // is tagged with, and what it means in the business glossary.
            //
            // StringCollection rather than String, because both are refiners and
            // a table carries more than one tag. A refiner buckets on the whole
            // stored value, so a joined "PII, GDPR" becomes a bucket that
            // filtering on PII does not match - and "show me everything tagged
            // PII" is the question these fields are registered to answer.
            PushSchema.Prop("classifications", PropertyType.StringCollection, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("glossaryTerms", PropertyType.StringCollection, queryable: true, retrievable: true, refinable: true),

            // The label the engine derives from those raw tags, when a
            // Sensitivity mapping is configured. Registered unconditionally and
            // written only when the mapping is on, because a registered schema
            // is append-only: a property added later cannot be PATCHed onto a
            // connection that has reached Ready, so the alternative to
            // registering it now is deleting the connection and every item in it
            // the day somebody wants the mapping.
            //
            // String, not StringCollection: one item has ONE label. Several
            // classifications collapse to the most restrictive one, and that is
            // the whole reason the mapping is an ordered list.
            PushSchema.Prop(
                SensitivityOptions.DefaultProperty,
                PropertyType.String,
                queryable: true,
                retrievable: true,
                refinable: true),

            PushSchema.Prop("upstream", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("downstream", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("columnCount", PropertyType.Int64, queryable: true, retrievable: true),
            PushSchema.Prop("modifiedUtc", PropertyType.DateTime, queryable: true, retrievable: true, label: Label.LastModifiedDateTime));
    }

    /// <inheritdoc/>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        CdpSettings settings = CdpSettings.From(options);

        settings.ValidateShared(errors);
        settings.ValidateAtlas(errors);

        if (settings.GroupMapping == GroupMappingMode.ExternalGroups)
        {
            errors.Add(
                "Settings:GroupMappingMode",
                "ExternalGroups is not implemented. An external group can only contain Entra users and groups, " +
                "so a cluster-local group whose members have no Entra identity cannot be mirrored into one that " +
                "grants anybody anything. Map the cluster's groups in Settings:EntraGroupMap instead.");
        }
    }

    /// <inheritdoc/>
    public IPushSource CreateSource(PushSourceContext context)
    {
        CdpSettings settings = CdpSettings.From(context.Options);

        GraphServiceClient? directory = context.Options.Setting("ResolveGroupsFromDirectory", false)
            ? new GraphServiceClient(context.Credential, ["https://graph.microsoft.com/.default"])
            : null;

        // The run's crawl state store, which is where a resolved cluster group is
        // remembered between runs. CdpCrawlState.cs says why it is published
        // rather than read off the context; the null store - no
        // Settings:StateConnectionString - keeps the pre-existing behaviour.
        var principals = new PrincipalResolver(
            PrincipalResolver.ParseMap(context.Options.Setting("EntraGroupMap")),
            directory,
            context.Log,
            CdpCrawlState.Current,
            CdpCrawlState.PrincipalCacheTtl(context.Options),
            context.IsDryRun);

        return new AtlasPushSource(
            settings,
            new AtlasClient(settings.AtlasBaseUrl, context.Log),
            new RangerPolicyClient(settings.RangerBaseUrl, context.Log),
            principals,
            new CheckpointStore(settings.CheckpointDirectory, this.Key, context.Log),
            context.Log);
    }
}
