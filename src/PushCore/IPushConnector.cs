// ---------------------------------------------------------------------------
// IPushConnector.cs
// The extension point. Everything a new source needs to describe, and nothing
// it does not.
//
// A connector answers three questions:
//   * what is this called, and which configuration file is mine
//   * what schema does the connection need
//   * where do the items come from
//
// It answers nothing about credentials, vaults, throttling, truncation, ACLs,
// exit codes or logging. Those are the engine's, identical for every source,
// and a connector that reimplemented one would be a bug.
//
// This interface names no source technology. A SQL connector implements
// ISqlPushConnector in PushCore.Sql, which supplies CreateSource for it from a
// query and a row mapping; a connector reading something that is not a database
// implements this directly and builds its own source. The engine sees only the
// two of them through this interface, which is why adding a source family costs
// the core nothing.
//
// Adding a connector is: implement this (or ISqlPushConnector), write an
// appsettings file, and compile it into an executable whose Program.cs is one
// line. No file in PushCore changes, and no other connector is touched.
// ---------------------------------------------------------------------------

namespace PushCore;

using Connector.Security.Configuration;
using Microsoft.Graph.Models.ExternalConnectors;

/// <summary>One source, described well enough for the engine to index it.</summary>
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
    /// Gets a value indicating whether every item this connector produces carries
    /// its own grants.
    ///
    /// False - the default - means every item gets the connection-wide ACL from
    /// Acl:GrantGroupObjectIds, which is therefore required in configuration.
    /// True means the source derives grants per item, so that setting is neither
    /// required nor read, and an item the source could resolve no group for is
    /// skipped rather than falling back to a connection-wide grant. There is no
    /// fallback on purpose: a fallback here would widen the audience of exactly
    /// the item whose permissions could not be established.
    /// </summary>
    bool ItemsCarryTheirOwnAcl => false;

    /// <summary>
    /// The external schema to register. Build it with <see cref="PushSchema"/> so
    /// the two irrecoverable rules are enforced before the first Graph call.
    /// </summary>
    /// <returns>The schema for this connector's connection.</returns>
    Schema BuildSchema();

    /// <summary>
    /// Opens whatever this connector reads and returns the items in it.
    ///
    /// The engine owns everything after an item is yielded - truncation, the
    /// ACL, the ID rules, the write with backoff - and owns the decision that an
    /// item counts, which it reports back through
    /// <see cref="IPushSource.OnItemCommittedAsync"/>. A source that advanced its
    /// own watermark would be able to skip a row that never reached the index.
    /// </summary>
    /// <param name="context">Configuration, credential and logger.</param>
    /// <returns>The source, disposed by the host when the run ends.</returns>
    IPushSource CreateSource(PushSourceContext context);

    /// <summary>
    /// Fills in the connector-specific half of the configuration the file left
    /// out. The shared half - connection ID, name, description - is applied by
    /// the host. The default implementation adds nothing.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    void ApplyDefaults(PushOptions options)
    {
    }

    /// <summary>
    /// Everything the host must check before this connector runs: the rules of
    /// its source family, then its own.
    ///
    /// A source family implements this once - PushCore.Sql validates the
    /// DataSource and Source sections here - and a connector adds its rules in
    /// <see cref="ValidateOptions"/> rather than overriding this, so a connector
    /// cannot accidentally drop its family's checks by defining a method.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator, so every problem is reported at once.</param>
    void Validate(PushOptions options, ValidationErrors errors)
    {
        this.ValidateOptions(options, errors);
    }

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
