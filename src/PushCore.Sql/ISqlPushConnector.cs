// ---------------------------------------------------------------------------
// ISqlPushConnector.cs
// The extension point for a source that is a SQL Server table or view.
//
// A SQL connector still answers only what is specific to it - a schema, a
// query, a row mapping - and this interface supplies the rest of IPushConnector
// on its behalf: how to open the source, what to default, and which
// configuration sections have to be valid before the run starts.
//
// Those three are explicit interface implementations on purpose. A connector
// class writes ValidateOptions to add its own rules; had this interface
// implemented Validate implicitly, a connector defining a method with that name
// would silently REPLACE its family's checks - the DataSource section, the view
// name, the vault secret - and the loss would look exactly like a passing
// build. Explicit implementation makes that impossible: the family's checks run
// and then call the connector's.
//
// Adding a SQL connector is still one class and one configuration file, and
// still changes nothing in PushCore or here.
// ---------------------------------------------------------------------------

namespace PushCore.Sql;

using Connector.Security.Configuration;
using Microsoft.Data.SqlClient;

/// <summary>One SQL source, described well enough for the engine to index it.</summary>
public interface ISqlPushConnector : IPushConnector
{
    /// <summary>
    /// The table or view this connector reads, used when configuration omits
    /// Source:ItemView. Declaring it here rather than defaulting it in the core
    /// is what lets an existing deployment's appsettings.json stay as it is.
    /// </summary>
    string DefaultItemView { get; }

    /// <summary>
    /// The query returning one row per external item.
    ///
    /// Anything interpolated into it must have been validated as an identifier
    /// first - a table or view cannot be a parameter, which is why
    /// <see cref="SourceSection.ItemView"/> is checked in configuration.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The T-SQL to execute.</returns>
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
    PushItem? MapRow(SqlDataReader reader, PushOptions options);

    /// <inheritdoc/>
    IPushSource IPushConnector.CreateSource(PushSourceContext context)
    {
        return new SqlPushSource(this, context);
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
        SqlSourceRules.Validate(options, errors);
        this.ValidateOptions(options, errors);
    }
}
