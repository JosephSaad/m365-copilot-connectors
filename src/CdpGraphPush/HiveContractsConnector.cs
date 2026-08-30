// ---------------------------------------------------------------------------
// HiveContractsConnector.cs
// One Hive table, indexed row by row - and the template for the next one.
//
// Adding a second table connector is this file with a different key, a
// different schema, a different row mapping and its own appsettings file.
// Nothing in PushCore or CdpConnector.Source changes, and neither does this
// connector: that is the same promise the SQL family makes, kept for a second
// source family.
//
// A deliberately rejected shape: one generic table connector whose schema comes
// from configuration. A Graph schema is per-connection and effectively
// append-only - a property cannot be removed, and refinability cannot be added
// after the fact - so a schema that drifts with a configuration file is a trap
// whose only exit is deleting the connection and every item in it. Each table
// declares its schema in code, where a change is a reviewed change.
// ---------------------------------------------------------------------------

namespace CdpGraphPush;

using CdpConnector.Source;
using CdpConnector.Source.Acl;
using CdpConnector.Source.Hive;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using Connector.Security.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;

/// <summary>Rows of a contracts table in Hive or Impala, one external item each.</summary>
public sealed class HiveContractsConnector : IPushConnector
{
    /// <inheritdoc/>
    public string Key => "cdphivecontracts";

    /// <inheritdoc/>
    public string DisplayName => "CDP Hive contracts";

    /// <inheritdoc/>
    public string DefaultConnectionId => "cdphivecontracts";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Cloudera Hive contracts";

    /// <inheritdoc/>
    public string DefaultDescription => "Contract records held in Hive on the Cloudera CDP cluster";

    /// <summary>
    /// Gets a value indicating that rows carry the table's grants rather than
    /// the connection's.
    ///
    /// Every row of an indexed table carries the SAME grants - the ones Ranger
    /// gives the table - so this could have used the connection-wide ACL. It
    /// does not, because the grants come from the cluster's policies rather than
    /// from a list in a configuration file, and a policy change should reach the
    /// index on the next run without anybody editing appsettings.
    /// </summary>
    public bool ItemsCarryTheirOwnAcl => true;

    /// <inheritdoc/>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            PushSchema.Prop("title", PropertyType.String, searchable: true, retrievable: true, label: Label.Title),
            PushSchema.Prop("contractRef", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("counterparty", PropertyType.String, searchable: true, retrievable: true),
            PushSchema.Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("owner", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("valueAmount", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("currency", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("startDate", PropertyType.DateTime, queryable: true, retrievable: true),
            PushSchema.Prop("endDate", PropertyType.DateTime, queryable: true, retrievable: true),
            PushSchema.Prop("modifiedUtc", PropertyType.DateTime, queryable: true, retrievable: true, label: Label.LastModifiedDateTime),
            PushSchema.Prop("sourceTable", PropertyType.String, queryable: true, retrievable: true, refinable: true));
    }

    /// <inheritdoc/>
    public void ApplyDefaults(PushOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            options.Source.ItemView = "contracts.contract";
        }
    }

    /// <inheritdoc/>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        CdpSettings settings = CdpSettings.From(options);

        settings.ValidateShared(errors);
        settings.ValidateHive(errors);

        // A table name is concatenated into the query, so it gets the same
        // treatment the SQL family gives a view name: it must be there, and it
        // must be an identifier. The shape rules are the shared Source
        // section's; that it is required at all is this connector's.
        options.Source.RequireItemView(errors, "Source");
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

        var reader = new HiveOdbcRowReader(
            HiveConnectionStringFactory.Build(settings),
            context.Options.DataSource.CommandTimeoutSeconds,
            context.Log);

        return new HiveTableSourceFactory(settings, principals, MapRow).Create(context, reader, this.Key);
    }

    /// <summary>
    /// Turns one row into an item.
    ///
    /// Returning null skips the row, which is what a row with no key does: an
    /// item ID has to be deterministic for the write to be an idempotent
    /// upsert, and there is nothing to derive one from.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The item, or null to skip.</returns>
    public static PushItem? MapRow(HiveRow row, PushOptions options)
    {
        string reference = row.Text("contract_ref");

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        string counterparty = row.Text("counterparty");

        var item = new PushItem
        {
            Id = HiveTableSourceFactory.ItemId(options.Source.ItemView, reference),
            ItemType = "contract",

            // The body is what somebody would search for in words. Column names
            // are included because "status open" is a query a person types.
            Content = string.Join(
                System.Environment.NewLine,
                $"Contract: {reference}",
                $"Counterparty: {counterparty}",
                $"Status: {row.Text("status")}",
                $"Owner: {row.Text("owner")}",
                $"Value: {row.Text("value_amount")} {row.Text("currency")}",
                row.Text("notes")),
        };

        item.AddIfPresent("title", $"{reference} - {counterparty}");
        item.AddIfPresent("contractRef", reference);
        item.AddIfPresent("counterparty", counterparty);
        item.AddIfPresent("status", row.Text("status"));
        item.AddIfPresent("owner", row.Text("owner"));
        item.AddIfPresent("valueAmount", row.Number("value_amount"));
        item.AddIfPresent("currency", row.Text("currency"));
        item.AddIfPresent("startDate", row.Timestamp("start_date")?.UtcDateTime.ToString("o"));
        item.AddIfPresent("endDate", row.Timestamp("end_date")?.UtcDateTime.ToString("o"));
        item.AddIfPresent("modifiedUtc", row.Timestamp("last_modified_ts")?.UtcDateTime.ToString("o"));
        item.AddIfPresent("sourceTable", options.Source.ItemView);

        return item;
    }
}
