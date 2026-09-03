// ---------------------------------------------------------------------------
// OracleRecordsPushConnector.cs
// An Oracle view, pushed straight to /external/connections/{id}/items/{itemId}.
//
// This is the whole connector. Credentials, the vault, connection and schema
// registration, truncation, ACLs, $batch writing, throttling, change detection,
// the delete sweep, checkpointing, exit codes and logging are the engine's, in
// PushCore - identical to the code the SQL and CDP push tools run.
//
// What is Oracle's alone is in three places: the connection string, the query
// dialect, and GuardAsync. The last is the important one - see its comment.
// ---------------------------------------------------------------------------

namespace OracleGraphPush;

using System.Data.Common;
using System.Globalization;
using Connector.Security.Configuration;
using Microsoft.Graph.Models.ExternalConnectors;
using Oracle.ManagedDataAccess.Client;
using PushCore;
using PushCore.Db;
using PushCore.State;

/// <summary>Records from an Oracle view, one item per row.</summary>
public sealed class OracleRecordsPushConnector : IDbPushConnector
{
    /// <summary>The vault key holding the database password, when one is used.</summary>
    public const string PasswordKey = "OraclePassword";

    /// <inheritdoc/>
    public string Key => "oracle";

    /// <inheritdoc/>
    public string DisplayName => "Records from an Oracle view";

    /// <inheritdoc/>
    public string DefaultConnectionId => "oraclerecords";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Oracle Records";

    /// <inheritdoc/>
    public string DefaultDescription => "Records ingested from Oracle";

    /// <inheritdoc/>
    public string DefaultItemView => "APP_RECORDS_V";

    /// <inheritdoc/>
    public DbProviderFactory Factory => OracleClientFactory.Instance;

    /// <inheritdoc/>
    /// <remarks>
    /// Null when DataSource:SqlAuthMode is Integrated, which on Oracle means
    /// Kerberos or a wallet and carries no password for the vault to hold.
    /// </remarks>
    public string? SecretKey =>
        this.UsesPassword ? PasswordKey : null;

    private bool UsesPassword { get; set; } = true;

    /// <summary>
    /// Six properties, matching the SQL connector's shape so an operator moving
    /// between the two reads one schema. Title and Url semantic labels plus a
    /// content payload are what make items eligible for the semantic index
    /// Copilot reads.
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

            PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url));
    }

    /// <inheritdoc/>
    public string BuildConnectionString(PushOptions options, string? secret)
    {
        ArgumentNullException.ThrowIfNull(options);

        // DataSource:Server carries an Easy Connect string or a TNS alias, which
        // is Oracle's equivalent of Server plus Database in one value. Database
        // is deliberately not read: appending it would produce a data source
        // Oracle cannot resolve, and silently.
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = options.DataSource.Server,
            ConnectionTimeout = options.DataSource.ConnectTimeoutSeconds,
        };

        if (this.UsesPassword)
        {
            builder.UserID = options.DataSource.SqlUserId;
            builder.Password = secret ?? string.Empty;
        }
        else
        {
            // External authentication: the wallet or the Kerberos ticket the
            // process already holds. No credential passes through this process.
            builder.UserID = "/";
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// LAST_MODIFIED, which makes this connector incremental.
    ///
    /// It is a claim about the view: the column must be UTC, monotonic and must
    /// move on every change. Where the source cannot promise that, override this
    /// to null and the connector reads in full every run, which is always safe.
    /// </summary>
    public string? WatermarkColumn => "LAST_MODIFIED";

    /// <inheritdoc/>
    public string BuildQuery(PushOptions options, CrawlMarker? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The view name is validated as an identifier by SourceSection, which is
        // what makes concatenating it safe. FETCH FIRST is 12c and later; this
        // connector does not support 11g, and says so rather than emitting
        // ROWNUM and hoping.
        string select =
            "SELECT RECORD_ID, TITLE, STATUS, OWNER, BODY, LAST_MODIFIED " +
            $"FROM {options.Source.ItemView}";

        var where = new List<string>();

        if (options.DataSource.SoftDeleteEnabled)
        {
            where.Add("IS_DELETED = 0");
        }

        if (resumeFrom is not null)
        {
            // The composite marker, spelled out. A predicate on the timestamp
            // alone either re-reads every row sharing the last second for ever,
            // or loses whichever of them had not been written when the run
            // stopped. Comparing the pair makes "strictly after the marker"
            // exact - and it is why the ORDER BY below must match it exactly.
            where.Add("(LAST_MODIFIED > :marker OR (LAST_MODIFIED = :marker AND RECORD_ID > :markerKey))");
        }

        if (where.Count > 0)
        {
            select += " WHERE " + string.Join(" AND ", where);
        }

        select += resumeFrom is not null
            ? " ORDER BY LAST_MODIFIED, RECORD_ID"
            : " ORDER BY RECORD_ID";

        if (options.Source.MaxItems > 0)
        {
            select += FormattableString.Invariant(
                $" FETCH FIRST {options.Source.MaxItems} ROWS ONLY");
        }

        return select;
    }

    /// <inheritdoc/>
    public void BindParameters(DbCommand command, PushOptions options, CrawlMarker? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (resumeFrom is null)
        {
            return;
        }

        // Oracle binds by name, and the same name appears twice in the
        // predicate, so BindByName has to be on or the second reference takes
        // the next positional value and the comparison silently reads the key
        // as a date.
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
        }

        Add("marker", resumeFrom.Value.MarkerTime);
        Add("markerKey", resumeFrom.Value.MarkerKey);

        void Add(string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>
    /// Refuses the crawl when Oracle enforces this view per session.
    ///
    /// Four features do that, and any one of them makes the service account's
    /// result set that account's own view rather than the table: VPD policies
    /// (ALL_POLICIES), Oracle Label Security (ALL_SA_TABLE_POLICIES), Real
    /// Application Security, and Data Redaction (REDACTION_POLICIES). Indexing
    /// under any of them publishes one identity's rows or unmasked columns to
    /// everyone the item is granted to.
    ///
    /// This is the same refusal CDP-17 makes for Ranger security zones, for the
    /// same reason and with the same absence of an override: a per-user policy
    /// has no representation in a static Graph ACL, so there is nothing to fall
    /// back to that would not be a guess.
    ///
    /// The catalogue views are the ALL_ ones rather than DBA_, because a
    /// least-privileged crawl identity is not a DBA and asking for DBA_ would
    /// make the guard fail on exactly the deployments that configured privilege
    /// correctly. ALL_ shows what this session can see, which is the right
    /// question: a policy this account cannot see is a policy that does not
    /// constrain it.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the view is cleared to read.</returns>
    public async ValueTask GuardAsync(
        DbConnection connection, PushOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        string view = Unqualified(options.Source.ItemView);

        var findings = new List<string>();

        await AddIfAny(
            "a Virtual Private Database policy",
            "SELECT COUNT(*) FROM ALL_POLICIES WHERE OBJECT_NAME = :name");

        await AddIfAny(
            "an Oracle Label Security policy",
            "SELECT COUNT(*) FROM ALL_SA_TABLE_POLICIES WHERE TABLE_NAME = :name");

        await AddIfAny(
            "a Data Redaction policy",
            "SELECT COUNT(*) FROM REDACTION_POLICIES WHERE OBJECT_NAME = :name");

        if (findings.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Oracle enforces '{options.Source.ItemView}' per session: it carries " +
            string.Join(" and ", findings) + ". " +
            "The rows and column values this crawl would read are the ones the crawl identity is " +
            "entitled to, not the ones every reader of the index is entitled to, so indexing them " +
            "would publish one identity's view to everyone granted the item. A per-session policy " +
            "has no representation in a static Graph permission, so this run stops rather than " +
            "reading a partial view and recording it as the whole table. Either exclude this view " +
            "from the crawl, or replace it with one that applies the restriction at rest and is " +
            "granted role-wise. There is no setting that disables this.");

        async Task AddIfAny(string description, string sql)
        {
            long count;

            try
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;

                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "name";
                parameter.Value = view;
                command.Parameters.Add(parameter);

                object? scalar = await command.ExecuteScalarAsync(cancellationToken);
                count = scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                // ORA-00942: the catalogue view does not exist, which is what an
                // estate without that option looks like - Label Security and
                // Data Redaction are separately licensed. Absent is not the same
                // as unreadable, and only absence is safe to treat as "none".
                return;
            }

            if (count > 0)
            {
                findings.Add(description);
            }
        }
    }

    /// <inheritdoc/>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        this.UsesPassword = !string.Equals(
            options.DataSource.SqlAuthMode, "Integrated", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(options.DataSource.Server))
        {
            errors.Add(
                "DataSource:Server",
                "is required: an Oracle Easy Connect string (host:port/service) or a TNS alias.");
        }

        if (this.UsesPassword && string.IsNullOrWhiteSpace(options.DataSource.SqlUserId))
        {
            errors.Add(
                "DataSource:SqlUserId",
                "is required unless DataSource:SqlAuthMode is Integrated, which on Oracle means a wallet or Kerberos.");
        }

        UrlTemplateValidator.Validate(errors, "DataSource:ItemUrlTemplate", options.DataSource.ItemUrlTemplate);
    }

    /// <inheritdoc/>
    public PushItem? MapRow(DbDataReader reader, PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        long? key = DbRead.Integer(reader, "RECORD_ID");

        if (key is null)
        {
            // A NULL key coalesced to anything would give every such row the same
            // item ID, and the PUT upsert would silently collapse them into one
            // item. Skip instead - the engine counts and reports skips.
            return null;
        }

        string id = key.Value.ToString(CultureInfo.InvariantCulture);

        var item = new PushItem
        {
            Id = "oraclerecord" + id,
            ItemType = "Record",
            Content = DbRead.Text(reader, "BODY"),
        };

        item.Properties["recordId"] = id;
        item.Properties["title"] = DbRead.Text(reader, "TITLE");
        item.Properties["status"] = DbRead.Text(reader, "STATUS");
        item.Properties["owner"] = DbRead.Text(reader, "OWNER");
        item.Properties["lastModified"] = DbRead.Utc(reader, "LAST_MODIFIED");

        // The engine advances the checkpoint to (LastModifiedUtc, Id), so a
        // marker-tier source that leaves this null checkpoints nothing and
        // silently reads in full for ever.
        item.LastModifiedUtc = DateTime.SpecifyKind(
            reader.GetDateTime(reader.GetOrdinal("LAST_MODIFIED")), DateTimeKind.Utc);
        item.Properties["url"] = string.Format(
            CultureInfo.InvariantCulture, options.DataSource.ItemUrlTemplate, id);

        return item;
    }

    /// <summary>Strips an owner prefix, because the catalogue views hold bare object names.</summary>
    /// <param name="view">The configured view name.</param>
    /// <returns>The object name alone, upper-cased as Oracle stores it.</returns>
    private static string Unqualified(string view)
    {
        string name = view;
        int dot = name.LastIndexOf('.');

        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        // Oracle folds unquoted identifiers to upper case, and the catalogue
        // stores them that way. A configuration written in lower case would
        // otherwise match nothing and the guard would pass on a protected view.
        return name.Trim('"').ToUpperInvariant();
    }
}
