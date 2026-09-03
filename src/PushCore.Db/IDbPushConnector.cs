// ---------------------------------------------------------------------------
// IDbPushConnector.cs
// The extension point for a source that is a table or view on any ADO.NET
// provider.
//
// This is ISqlPushConnector's shape with the three SqlClient types widened to
// their base classes - DbDataReader for the row, DbProviderFactory for the
// connection - and one member added that SQL Server did not need: the connector
// builds its own connection string. That member is what makes the interface
// provider-agnostic without this project referencing a driver. Oracle spells a
// service name and Teradata spells a host differently enough that a shared
// builder would be a switch statement over providers, which is the thing an
// abstraction is supposed to remove.
//
// Validate is an explicit interface implementation for exactly the reason
// ISqlPushConnector documents: a connector that defined a method called Validate
// would otherwise silently REPLACE its family's checks, and the loss would look
// like a passing build.
//
// Adding a connector here is one class and one configuration file. Nothing in
// PushCore changes, and nothing in PushCore.Sql changes.
// ---------------------------------------------------------------------------

namespace PushCore.Db;

using System.Data.Common;
using Connector.Security.Configuration;

/// <summary>One relational source on any provider, described well enough for the engine to index it.</summary>
public interface IDbPushConnector : IPushConnector
{
    /// <summary>
    /// The provider's factory - OracleClientFactory, TdFactory, and so on.
    ///
    /// Supplied by the connector rather than resolved from a registered name so
    /// that this project depends on no driver, and so a connector cannot fail at
    /// run time on a provider nobody registered.
    /// </summary>
    DbProviderFactory Factory { get; }

    /// <summary>
    /// The table or view this connector reads, used when configuration omits
    /// Source:ItemView.
    /// </summary>
    string DefaultItemView { get; }

    /// <summary>
    /// Builds the connection string for this provider.
    ///
    /// The secret, when the connector asked for one, has already been resolved
    /// and is passed in rather than fetched here - a connector must not reach
    /// for a credential store on its own, because the engine's redaction and
    /// caching both sit on the path that does it.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <param name="secret">The resolved secret, or null when none was required.</param>
    /// <returns>A provider-specific connection string.</returns>
    string BuildConnectionString(PushOptions options, string? secret);

    /// <summary>
    /// The name of the vault secret this connector needs, or null when its
    /// authentication carries no password - Kerberos, or a wallet.
    /// </summary>
    string? SecretKey => null;

    /// <summary>
    /// Runs on the open connection before the query, and throws to refuse the
    /// crawl.
    ///
    /// This is RangerPolicyClient.RefuseSecurityZones' place on this path, and
    /// it exists for the same reason. Oracle's VPD, Label Security and data
    /// redaction, and Teradata's row-level security constraints, are all
    /// enforced per session: the rows the service account sees are the rows ITS
    /// policy admits, so indexing them publishes one identity's view of the
    /// data to everyone granted the item. A static Graph ACL cannot express any
    /// of it, so the honest answer is to stop rather than to read a partial
    /// view and call it the whole table.
    ///
    /// It is a connection-time check rather than a configuration one because
    /// the answer lives in the database's catalogue, not in appsettings.json,
    /// and because a policy added after go-live has to be caught on the next
    /// crawl rather than at the next redeployment.
    ///
    /// Default is no-op: a provider with no per-user enforcement overrides
    /// nothing.
    /// </summary>
    /// <param name="connection">The open connection, before the query runs.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the source is cleared to read.</returns>
    ValueTask GuardAsync(DbConnection connection, PushOptions options, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The query returning one row per external item.
    ///
    /// Anything interpolated into it must have been validated as an identifier
    /// first: a table or view cannot be a parameter, which is why
    /// <see cref="SourceSection.ItemView"/> is checked in configuration.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The SQL to execute, in this provider's dialect.</returns>
    string BuildQuery(PushOptions options);

    /// <summary>
    /// Turns the reader's current row into an item, or returns null to skip it.
    ///
    /// Set the content and properties; the engine applies truncation, the ACL,
    /// the ID rules and the write. Do not read a second row.
    /// </summary>
    /// <param name="reader">Positioned on the row to map.</param>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The item, or null to skip this row.</returns>
    PushItem? MapRow(DbDataReader reader, PushOptions options);

    /// <inheritdoc/>
    IPushSource IPushConnector.CreateSource(PushSourceContext context)
    {
        return new DbPushSource(this, context);
    }

    /// <inheritdoc/>
    void IPushConnector.ApplyDefaults(PushOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            options.Source.ItemView = this.DefaultItemView;
        }
    }

    /// <inheritdoc/>
    void IPushConnector.Validate(PushOptions options, ValidationErrors errors)
    {
        DbSourceRules.Validate(this, options, errors);
        this.ValidateOptions(options, errors);
    }
}
