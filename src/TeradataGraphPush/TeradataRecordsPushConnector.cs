// ---------------------------------------------------------------------------
// TeradataRecordsPushConnector.cs
// A Teradata view, pushed straight to /external/connections/{id}/items/{itemId}.
//
// The whole connector. Credentials, the vault, connection and schema
// registration, truncation, ACLs, $batch writing, throttling, change detection,
// the delete sweep, checkpointing, exit codes and logging are the engine's.
//
// Teradata's own three things are the connection string, the dialect, and
// GuardAsync - which refuses a table carrying a row- or column-level security
// constraint, for the same reason the Oracle connector refuses a VPD policy.
// ---------------------------------------------------------------------------

namespace TeradataGraphPush;

using System.Data.Common;
using System.Globalization;
using Connector.Security.Configuration;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;
using PushCore.Db;
using PushCore.State;
using Teradata.Client.Provider;

/// <summary>Records from a Teradata view, one item per row.</summary>
public sealed class TeradataRecordsPushConnector : IDbPushConnector
{
    /// <summary>The vault key holding the database password, when one is used.</summary>
    public const string PasswordKey = "TeradataPassword";

    /// <inheritdoc/>
    public string Key => "teradata";

    /// <inheritdoc/>
    public string DisplayName => "Records from a Teradata view";

    /// <inheritdoc/>
    public string DefaultConnectionId => "teradatarecords";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Teradata Records";

    /// <inheritdoc/>
    public string DefaultDescription => "Records ingested from Teradata";

    /// <inheritdoc/>
    public string DefaultItemView => "APP_RECORDS_V";

    /// <inheritdoc/>
    public DbProviderFactory Factory => TdFactory.Instance;

    /// <inheritdoc/>
    public string? SecretKey => this.UsesPassword ? PasswordKey : null;

    private bool UsesPassword { get; set; } = true;

    /// <summary>
    /// The same six properties the SQL and Oracle connectors publish, so an
    /// operator reading three connections reads one schema.
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

        var builder = new TdConnectionStringBuilder
        {
            DataSource = options.DataSource.Server,
            ConnectionTimeout = options.DataSource.ConnectTimeoutSeconds,
        };

        if (!string.IsNullOrWhiteSpace(options.DataSource.Database))
        {
            builder.Database = options.DataSource.Database;
        }

        if (this.UsesPassword)
        {
            builder.UserId = options.DataSource.SqlUserId;
            builder.Password = secret ?? string.Empty;

            // TD2 is Teradata's own directory. Named rather than defaulted so a
            // deployment that means LDAP has to say LDAP and cannot get TD2 by
            // omission.
            builder.AuthenticationMechanism = "TD2";
        }
        else
        {
            // Kerberos: the ticket the process already holds. No credential
            // passes through this process.
            builder.AuthenticationMechanism = "KRB5";
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// LAST_MODIFIED, which makes this connector incremental. Same claim about
    /// the source as the Oracle connector's: UTC, monotonic, and moving on every
    /// change. Override to null where the table cannot promise it.
    /// </summary>
    public string? WatermarkColumn => "LAST_MODIFIED";

    /// <inheritdoc/>
    public string BuildQuery(PushOptions options, CrawlMarker? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(options);

        // TOP binds before ORDER BY in Teradata, which is what makes the two
        // usable together - unlike the FETCH FIRST the Oracle connector emits.
        string top = options.Source.MaxItems > 0
            ? FormattableString.Invariant($"TOP {options.Source.MaxItems} ")
            : string.Empty;

        string select =
            $"SELECT {top}RECORD_ID, TITLE, STATUS, OWNER, BODY, LAST_MODIFIED " +
            $"FROM {options.Source.ItemView}";

        var where = new List<string>();

        if (options.DataSource.SoftDeleteEnabled)
        {
            where.Add("IS_DELETED = 0");
        }

        if (resumeFrom is not null)
        {
            // Positional parameters, so the marker appears twice and is bound
            // twice, in the order written. See BindParameters.
            where.Add("(LAST_MODIFIED > ? OR (LAST_MODIFIED = ? AND RECORD_ID > ?))");
        }

        if (where.Count > 0)
        {
            select += " WHERE " + string.Join(" AND ", where);
        }

        return select + (resumeFrom is not null
            ? " ORDER BY LAST_MODIFIED, RECORD_ID"
            : " ORDER BY RECORD_ID");
    }

    /// <inheritdoc/>
    public void BindParameters(DbCommand command, PushOptions options, CrawlMarker? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (resumeFrom is null)
        {
            return;
        }

        // Teradata binds by POSITION, so the marker is added twice rather than
        // once and referenced twice. Adding it once would leave the third
        // placeholder unbound and the second reading the key as a date.
        Add(resumeFrom.Value.MarkerTime);
        Add(resumeFrom.Value.MarkerTime);
        Add(resumeFrom.Value.MarkerKey);

        void Add(object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>
    /// Refuses the crawl when Teradata enforces this table per session.
    ///
    /// Teradata's row- and column-level security is a CONSTRAINT: a constraint
    /// is defined in DBC.SecConstraintsV, and a table subject to it carries a
    /// column of that constraint's name. UDFs then evaluate the constraint per
    /// session, so the rows and column values this crawl reads are the ones the
    /// crawl identity is entitled to and not the ones every reader of the index
    /// is entitled to.
    ///
    /// The check is therefore an intersection: columns of this table whose name
    /// matches a defined security constraint. That catches both the row-level
    /// and the column-level form, because both are expressed the same way.
    ///
    /// Same refusal as CDP-17 and the Oracle guard, and for the same reason: a
    /// per-session rule has no representation in a static Graph permission, so
    /// there is nothing to fall back to that would not be a guess.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="options">Validated configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the table is cleared to read.</returns>
    public async ValueTask GuardAsync(
        DbConnection connection, PushOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        (string database, string table) = Split(options.Source.ItemView, options.DataSource.Database);

        long constrained;

        try
        {
            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT COUNT(*) FROM DBC.ColumnsV c " +
                "WHERE c.TableName = ? AND (? = '' OR c.DatabaseName = ?) " +
                "AND c.ColumnName IN (SELECT ConstraintName FROM DBC.SecConstraintsV)";

            Add(command, table);
            Add(command, database);
            Add(command, database);

            object? scalar = await command.ExecuteScalarAsync(cancellationToken);
            constrained = scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        catch (TdException ex)
        {
            // Reading DBC needs a grant the crawl identity may not hold. Unlike
            // Oracle's ORA-00942, this is NOT "the feature is not installed" -
            // DBC.SecConstraintsV exists on every system. An unreadable
            // catalogue is an unknown answer, and an unknown answer to "is this
            // enforced per user" has to fail closed.
            throw new InvalidOperationException(
                $"Could not read DBC.SecConstraintsV to establish whether '{options.Source.ItemView}' carries a " +
                "security constraint. The crawl identity needs SELECT on DBC.ColumnsV and DBC.SecConstraintsV. " +
                "This run stops rather than assuming there is no constraint: if there is one, indexing would " +
                "publish the crawl identity's rows to everyone granted the item.",
                ex);
        }

        if (constrained == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Teradata enforces '{options.Source.ItemView}' per session: it carries {constrained} " +
            "row- or column-level security constraint column(s). The rows and values this crawl would read " +
            "are the ones the crawl identity is entitled to, not the ones every reader of the index is " +
            "entitled to, so indexing them would publish one identity's view to everyone granted the item. " +
            "A per-session constraint has no representation in a static Graph permission, so this run stops. " +
            "Either exclude this object from the crawl, or replace it with a view that applies the " +
            "restriction at rest and is granted role-wise. There is no setting that disables this.");

        static void Add(DbCommand command, string value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
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
            errors.Add("DataSource:Server", "is required: the Teradata system name or COP alias.");
        }

        if (this.UsesPassword && string.IsNullOrWhiteSpace(options.DataSource.SqlUserId))
        {
            errors.Add(
                "DataSource:SqlUserId",
                "is required unless DataSource:SqlAuthMode is Integrated, which on Teradata means Kerberos.");
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
            // A NULL key would collapse every such row onto one item ID under
            // the PUT upsert. Skip; the engine counts and reports skips.
            return null;
        }

        string id = key.Value.ToString(CultureInfo.InvariantCulture);

        var item = new PushItem
        {
            Id = "teradatarecord" + id,
            ItemType = "Record",
            Content = DbRead.Text(reader, "BODY"),
        };

        item.Properties["recordId"] = id;
        item.Properties["title"] = DbRead.Text(reader, "TITLE");
        item.Properties["status"] = DbRead.Text(reader, "STATUS");
        item.Properties["owner"] = DbRead.Text(reader, "OWNER");
        item.Properties["lastModified"] = DbRead.Utc(reader, "LAST_MODIFIED");

        // The engine checkpoints (LastModifiedUtc, Id); leaving this null makes
        // a marker-tier source read in full for ever without saying so.
        item.LastModifiedUtc = DateTime.SpecifyKind(
            reader.GetDateTime(reader.GetOrdinal("LAST_MODIFIED")), DateTimeKind.Utc);
        item.Properties["url"] = string.Format(
            CultureInfo.InvariantCulture, options.DataSource.ItemUrlTemplate, id);

        return item;
    }

    /// <summary>Splits a possibly qualified object name into database and table.</summary>
    /// <param name="view">The configured object name.</param>
    /// <param name="fallback">DataSource:Database, used when the name is unqualified.</param>
    /// <returns>The database and the bare table name.</returns>
    private static (string Database, string Table) Split(string view, string fallback)
    {
        int dot = view.LastIndexOf('.');

        return dot >= 0 && dot < view.Length - 1
            ? (view[..dot].Trim('"'), view[(dot + 1)..].Trim('"'))
            : (fallback ?? string.Empty, view.Trim('"'));
    }
}
