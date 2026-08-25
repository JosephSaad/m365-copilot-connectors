// ---------------------------------------------------------------------------
// IPushConnector.cs
// The extension point. Everything a new SQL source needs to describe, and
// nothing it does not.
//
// A connector answers four questions:
//   * what is this called, and which configuration file is mine
//   * what schema does the connection need
//   * what query returns one row per item
//   * how does a row become an item
//
// It answers nothing about credentials, vaults, SQL connections, throttling,
// truncation, ACLs, exit codes or logging. Those are the engine's, identical
// for every source, and a connector that reimplemented one would be a bug.
//
// Adding a connector is: implement this, write an appsettings file, add one
// line to a Program.cs. No file in SqlPushCore changes, and no other connector
// is touched or even rebuilt differently.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using SqlTicketsConnector.Security.Configuration;

/// <summary>One SQL source, described well enough for the engine to index it.</summary>
public interface IPushConnector
{
    /// <summary>
    /// Short alphanumeric name, used to select this connector on the command line
    /// and to find its configuration file. Must be unique within an executable.
    ///
    /// The engine reads appsettings.{Key}.json when that file exists and
    /// appsettings.json when it does not, so a second connector added to an
    /// existing executable gets its own file and the first one's is untouched.
    /// </summary>
    string Key { get; }

    /// <summary>What this indexes, in a few words. Appears in logs and in --help.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The external connection this connector owns, used when configuration does
    /// not name one. It is also how the host stops a connector being pointed at
    /// a neighbour's connection: a configured ID matching another hosted
    /// connector's default fails validation, because two schemas cannot share a
    /// connection and a registered schema cannot be replaced.
    /// </summary>
    string DefaultConnectionId { get; }

    /// <summary>Display name for the connection, used when configuration omits one.</summary>
    string DefaultConnectionName { get; }

    /// <summary>Connection description, used when configuration omits one.</summary>
    string DefaultDescription => string.Empty;

    /// <summary>
    /// The table or view this connector reads, used when configuration omits
    /// Source:ItemView. Declaring it here rather than defaulting it in the core
    /// is what lets an existing deployment's appsettings.json stay as it is.
    /// </summary>
    string DefaultItemView { get; }

    /// <summary>
    /// The external schema to register. Build it with <see cref="PushSchema"/> so
    /// the two irrecoverable rules are enforced before the first Graph call.
    /// </summary>
    /// <returns>The schema for this connector's connection.</returns>
    Schema BuildSchema();

    /// <summary>
    /// The query returning one row per external item.
    ///
    /// Anything interpolated into it must have been validated as an identifier
    /// first — a table or view cannot be a parameter, which is why
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

    /// <summary>
    /// Adds any configuration rules of this connector's own, on top of the ones
    /// every connector shares. The default implementation adds none.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator, so every problem is reported at once.</param>
    void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
    }
}
