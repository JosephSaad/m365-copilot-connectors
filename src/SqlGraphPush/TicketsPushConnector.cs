// ---------------------------------------------------------------------------
// TicketsPushConnector.cs
// dbo.Tickets, pushed straight to /external/connections/{id}/items/{itemId},
// bypassing the Graph connector agent. Used to seed or repair a connection.
//
// This is the whole connector. Credentials, the vault, the SQL connection,
// connection and schema registration, truncation, ACLs, throttling, exit codes
// and logging are the engine's, in SqlPushCore.
//
// Unlike the agent-hosted connector, this path does call Microsoft Graph, so
// certificate authentication here is for Graph. Application permissions remain
// ExternalConnection.ReadWrite.OwnedBy and ExternalItem.ReadWrite.OwnedBy,
// granted with admin consent, with the public certificate uploaded to the app
// registration. A client secret is supported as an alternative (Auth:Mode),
// read from Windows Credential Manager; no secret value appears in
// configuration.
// ---------------------------------------------------------------------------

namespace SqlGraphPush;

using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using SqlPushCore;

/// <summary>Support tickets, one item per row of dbo.Tickets.</summary>
public sealed class TicketsPushConnector : IPushConnector
{
    /// <inheritdoc/>
    public string Key => "tickets";

    /// <inheritdoc/>
    public string DisplayName => "Support tickets from dbo.Tickets";

    /// <inheritdoc/>
    public string DefaultConnectionId => "sqltickets";

    /// <inheritdoc/>
    public string DefaultConnectionName => "SQL Support Tickets";

    /// <inheritdoc/>
    public string DefaultDescription => "Support tickets ingested from SQL Server";

    /// <inheritdoc/>
    public string DefaultItemView => "dbo.Tickets";

    /// <summary>
    /// Six properties. Title and Url semantic labels plus a content payload are
    /// what make items eligible for the semantic index that Copilot reads.
    /// </summary>
    /// <returns>The schema.</returns>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            PushSchema.Prop("ticketId", PropertyType.String, queryable: true, retrievable: true),

            PushSchema.Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.Title),

            // Refinable rather than searchable: a status is filtered by, not
            // typed. The two annotations are mutually exclusive.
            PushSchema.Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            PushSchema.Prop("assignedTo", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                label: Label.LastModifiedDateTime),
            PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url));
    }

    /// <summary>
    /// The whole table, so no watermark predicate. Soft deleted rows are
    /// excluded rather than pushed and then removed - which does mean a row that
    /// becomes deleted leaves its item behind in the index. That is a property
    /// of the direct push model; deploy/Compare-SourceToIndex.ps1 finds them.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The query.</returns>
    public string BuildQuery(PushOptions options)
    {
        // The table name is validated as an identifier in SourceSection.Validate,
        // which is what makes concatenating it safe.
        string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

        string select =
            $"SELECT {top}TicketId, Title, Status, AssignedTo, Body, LastModified FROM {options.Source.ItemView}";

        return options.DataSource.SoftDeleteEnabled
            ? select + " WHERE IsDeleted = 0 ORDER BY TicketId;"
            : select + " ORDER BY TicketId;";
    }

    /// <inheritdoc/>
    public PushItem? MapRow(SqlDataReader reader, PushOptions options)
    {
        int ticketId = SqlRead.Integer(reader, "TicketId") ?? 0;

        // Alphanumeric, 128 character maximum. Composed rather than reusing the
        // key directly so the rule holds whatever the key turns out to look like.
        var item = new PushItem
        {
            Id = "ticket" + ticketId.ToString(CultureInfo.InvariantCulture),
            ItemType = "Ticket",
            Content = SqlRead.Text(reader, "Body"),
        };

        item.Properties["ticketId"] = ticketId.ToString(CultureInfo.InvariantCulture);
        item.Properties["title"] = SqlRead.Text(reader, "Title");
        item.Properties["status"] = SqlRead.Text(reader, "Status");
        item.Properties["assignedTo"] = SqlRead.Text(reader, "AssignedTo");
        item.Properties["lastModified"] = SqlRead.Utc(reader, "LastModified");
        item.Properties["url"] = string.Format(
            CultureInfo.InvariantCulture, options.DataSource.ItemUrlTemplate, ticketId);

        return item;
    }
}
