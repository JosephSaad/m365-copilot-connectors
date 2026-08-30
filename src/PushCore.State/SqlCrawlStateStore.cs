// ---------------------------------------------------------------------------
// SqlCrawlStateStore.cs
// The crawl's memory, held in ConnectorState, reached only through sql/23.
//
// This is ICrawlStateStore over the seventeen procedures sql/25 grants to
// crawl_writer. There is no table access behind it and there must not be: the
// same file DENYs SELECT, INSERT, UPDATE and DELETE on SCHEMA::crawl to that
// role, so a query added to this class does not fail review, it fails at
// runtime, in the middle of a run, on the customer's server. Everything below
// is an EXEC of a named procedure for that reason.
//
// THREE DECISIONS SHAPE THE WHOLE FILE.
//
// 1. A connection per call, not one shared. Several writer threads call
//    RecordWrittenAsync and RecordUnchangedAsync at once - that is what the
//    engine's concurrency means - and SqlConnection is not thread-safe. Sharing
//    one would either serialise the writers behind a lock, giving back the
//    throughput this whole design exists to buy, or interleave two commands on
//    one TDS stream, which is a connection reset rather than an error anyone can
//    read. SqlClient's own pool makes opening cheap: the handshake is paid once
//    per pooled connection, and every call after that is a fetch from the pool.
//    The one exception is the run-closing sequence, which holds a single
//    connection across four commands because it runs on one thread after the
//    writers have finished.
//
// 2. Batches go as table-valued parameters, never a row per call. At the rates
//    batching and concurrency make possible, a round trip beside each Graph
//    write would be the new bottleneck, and it would be invisible - the timing
//    table would charge it to Commit, where nobody is looking. The rows are
//    streamed as SqlDataRecord rather than materialised into a DataTable, so a
//    hundred-thousand-item batch costs one row of memory rather than a second
//    copy of the batch.
//
// 3. Recording follows the write. Every method here is called after Graph has
//    confirmed, and the failure mode of getting that backwards is the worst one
//    in the design: a hash recorded before the PUT means the next run sees the
//    item as unchanged and skips it, so a single failure between the two turns
//    into an item that is permanently stale AND permanently invisible. This
//    class cannot enforce that ordering - the engine owns it - but nothing here
//    writes anything the engine has not already told it happened.
//
// WHAT THIS STORE REFUSES TO DO. It never widens the sweep, never moves a
// marker forward past what it was given, and never turns a guard into a retry.
// Where a decision could go either way, this class takes the reading that costs
// a re-read rather than the one that costs a deletion.
//
// WHAT IT DOES NOT DO ANY MORE. An earlier draft carried client-side
// compensations for six places where sql/23 and ICrawlStateStore did not line
// up - a checkpoint probe before opening a run, a liveness filter over
// uspGetItemState, a skip for principal keys too long for the lookup type. All
// six were fixed in the SQL, and the compensations are gone rather than kept as
// belt and braces: a guard that duplicates one in the database is a guard
// nobody maintains, and the day the two disagree the C# one wins silently.
// ---------------------------------------------------------------------------

namespace PushCore.State;

using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using Connector.Security.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Connector.Security.Logging;
using Serilog;

/// <summary>Durable crawl memory, held in the ConnectorState database.</summary>
/// <remarks>
/// One instance per run. It learns the connection and the run ID from
/// <see cref="BeginRunAsync"/> and holds nothing else across calls except the
/// throttle buffer, so two runs in one process need two instances.
/// </remarks>
public sealed class SqlCrawlStateStore : ICrawlStateStore
{
    /// <summary>The error sql/23 raises when the delete guard trips.</summary>
    /// <remarks>
    /// Named rather than inlined because the number is the contract: the
    /// procedure's message carries the counts an operator needs, and catching it
    /// by number is what lets this class hand that message on intact instead of
    /// replacing it with one of its own.
    /// </remarks>
    private const int DeleteGuardErrorNumber = 50007;

    /// <summary>
    /// Command timeout for every call, in seconds.
    /// </summary>
    /// <remarks>
    /// Longer than SqlClient's thirty-second default because the delete sweep
    /// counts the whole live inventory for a connection, and a million-row
    /// inventory on a busy instance legitimately outlives thirty seconds. Still
    /// bounded, because a wedged call must fail inside the run rather than hold
    /// the run open until the abandoned-run reaper closes it twelve hours later.
    /// </remarks>
    private const int CommandTimeoutSeconds = 120;

    /// <summary>Widths that the table types in sql/20 declare.</summary>
    /// <remarks>
    /// Repeated here because a value wider than the column does not round-trip:
    /// SQL Server truncates or refuses, and a truncated item ID is a different
    /// item. Every value that goes into a table-valued parameter is measured
    /// against these before it is sent.
    /// </remarks>
    private const int ItemIdWidth = 128;

    private const int ItemTypeWidth = 64;

    private const int MarkerKeyWidth = 256;

    private const int SourceTypeWidth = 32;

    private const int SourceKeyWidth = 256;

    private const int ErrorKindWidth = 64;

    private const int ErrorMessageWidth = 2000;

    private const int EndpointWidth = 32;

    /// <summary>How many throttle events may be buffered before further ones are counted and dropped.</summary>
    /// <remarks>
    /// A bound rather than a growing list, for the reason PushTiming keeps a
    /// fixed histogram: the run that produces the most throttle events is by
    /// definition the run already in trouble, and a buffer that grows without
    /// limit turns a throttled run into an out-of-memory one. Anything past the
    /// cap is counted, and the count is reported when the run closes, so the
    /// operator sees "more than this many" rather than a quietly short list.
    /// </remarks>
    private const int ThrottleBufferLimit = 100_000;

    private readonly string? connectionString;

    private readonly SqlConnectionFactory? connections;

    private readonly ILogger log;

    private readonly ConcurrentQueue<ThrottleEvent> throttles = new ConcurrentQueue<ThrottleEvent>();

    private int throttlesBuffered;

    private int throttlesDropped;

    private string? connectionId;

    private long runId;

    private int runClosed;

    /// <summary>Initializes a new instance of the <see cref="SqlCrawlStateStore"/> class over a connection string.</summary>
    /// <param name="connectionString">
    /// A connection string for the ConnectorState database, as the crawl_writer
    /// member sql/25 creates.
    /// </param>
    /// <param name="logger">Log destination. Null falls back to the global logger.</param>
    /// <remarks>
    /// The connection string is held in managed memory for the lifetime of the
    /// store and is never logged. It is a separate string from the source
    /// database's on purpose - sql/20 explains why the crawl state does not live
    /// in Ops - so a deployment configures two, and a deployment that configures
    /// only one gets NullCrawlStateStore and its previous behaviour.
    /// </remarks>
    public SqlCrawlStateStore(string connectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A connection string for the ConnectorState database is required. " +
                "A connector with no state database configured uses NullCrawlStateStore instead.",
                nameof(connectionString));
        }

        this.connectionString = connectionString;
        this.log = logger ?? Log.Logger;
    }

    /// <summary>Initializes a new instance of the <see cref="SqlCrawlStateStore"/> class over the shared factory.</summary>
    /// <param name="connections">
    /// A factory configured for the ConnectorState database, not for the source.
    /// </param>
    /// <param name="logger">Log destination. Null falls back to the global logger.</param>
    /// <remarks>
    /// The overload that exists for Entra-authenticated deployments. The factory
    /// acquires the token and implements the rotation rule - an authentication
    /// failure invalidates the cached secret and retries exactly once - so a
    /// password rotation is invisible to a crawl in progress rather than a run
    /// that fails at its first write.
    /// </remarks>
    public SqlCrawlStateStore(SqlConnectionFactory connections, ILogger logger)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.log = logger ?? Log.Logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// True, unconditionally. This store exists because a state database was
    /// configured; the question the engine is really asking is answered by which
    /// implementation it was handed, not by anything this one could discover at
    /// run time.
    /// </remarks>
    public bool IsEnabled => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Two calls, in the order sql/23 requires: uspRegisterConnection is
    /// idempotent and must precede uspBeginRun, which throws 50002 for a
    /// connection it has never seen.
    ///
    /// THE MODE THIS RETURNS MAY NOT BE THE MODE THAT WAS ASKED FOR, and the
    /// decision is the procedure's rather than this class's. uspBeginRun works
    /// out whether a full crawl is due - never had one, aged out, or no
    /// checkpoint to resume from - and inserts the run with the mode it will
    /// ACTUALLY read in, then returns that mode in the result set. This method
    /// reports it and warns when it differs; it does not second-guess it and
    /// does not probe for the answer beforehand.
    ///
    /// That the row records the escalated mode is what makes the escalation
    /// worth anything: uspGetPendingDeletes reads Run.Mode, and
    /// LastFullSuccessUtc only advances for a run recorded as full. A store that
    /// escalated only its return value would produce a run that read the whole
    /// source, was filed as incremental, had its sweep refused, and never
    /// advanced the baseline - so every later run would be escalated too, for
    /// ever, with no sweep ever running.
    /// </remarks>
    /// <inheritdoc/>
    /// <remarks>
    /// One round trip, before the run is opened. The procedure advances the
    /// stored version as it answers, so this reports a change exactly once -
    /// see sql/28, where that decision and its consequence are set out.
    ///
    /// A missing procedure is treated as "no change" rather than thrown. sql/28
    /// is a later addition to a database that may already be deployed, and an
    /// operator who has not run it has an out-of-date schema, not a broken
    /// crawl. Refusing to run would turn a missing migration into an outage;
    /// the log line says what is not being checked.
    /// </remarks>
    public async Task<bool> CheckHashVersionAsync(
        string connectionId,
        int hashVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        }

        try
        {
            await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlCommand check = Procedure(sql, "crawl.uspCheckHashVersion");

            check.Parameters.Add(Text("@ConnectionId", connectionId, 64));
            check.Parameters.Add(new SqlParameter("@HashVersion", SqlDbType.TinyInt) { Value = (byte)hashVersion });

            await using SqlDataReader reader =
                await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            bool changed = reader.GetBoolean(reader.GetOrdinal("WasChanged"));

            if (changed)
            {
                this.log.Warning(
                    "The hash framing changed from version {Previous} to {Current}. Every hash on record was " +
                    "computed by the previous one and none of them will match, so this run is escalated to full " +
                    "and will rewrite the corpus. This is a migration, not a fault - but it costs a full write " +
                    "cycle, and it happens once.",
                    reader.GetByte(reader.GetOrdinal("PreviousVersion")),
                    reader.GetByte(reader.GetOrdinal("CurrentVersion")));
            }

            return changed;
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            // 2812: could not find stored procedure. sql/28 has not been run.
            this.log.Warning(
                "crawl.uspCheckHashVersion is not present, so the hash framing version is not being checked. " +
                "Run sql/28 against ConnectorState. Until then a change to the hasher would rewrite the whole " +
                "corpus silently, which is the thing that script exists to make visible.");

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<CrawlRunStart> BeginRunAsync(
        CrawlConnectionInfo connection,
        CrawlMode requested,
        bool dryRun,
        int fullEveryHours,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.ConnectionId))
        {
            throw new ArgumentException("A connection ID is required.", nameof(connection));
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (SqlCommand register = Procedure(sql, "crawl.uspRegisterConnection"))
        {
            register.Parameters.Add(Text("@ConnectionId", connection.ConnectionId, 64));
            register.Parameters.Add(Text("@ConnectorKey", connection.ConnectorKey, 64));
            register.Parameters.Add(Text("@DisplayName", connection.DisplayName, 256));
            register.Parameters.Add(Optional("@ExpectedIntervalMinutes", SqlDbType.Int, connection.ExpectedIntervalMinutes));

            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqlCommand begin = Procedure(sql, "crawl.uspBeginRun");

        begin.Parameters.Add(Text("@ConnectionId", connection.ConnectionId, 64));
        begin.Parameters.Add(new SqlParameter("@Mode", SqlDbType.TinyInt) { Value = (byte)requested });
        begin.Parameters.Add(Text("@HostName", Environment.MachineName, 128));
        begin.Parameters.Add(new SqlParameter("@ProcessId", SqlDbType.Int) { Value = Environment.ProcessId });
        begin.Parameters.Add(Text("@ToolVersion", ToolVersion(), 64));
        begin.Parameters.Add(new SqlParameter("@IsDryRun", SqlDbType.Bit) { Value = dryRun });
        begin.Parameters.Add(new SqlParameter("@FullEveryHours", SqlDbType.Int) { Value = fullEveryHours });

        long openedRunId;
        CrawlMode actual;
        bool fullCrawlDue;
        DateTime? lastFullSuccessUtc;
        int reaped;

        await using (SqlDataReader reader = await begin.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "crawl.uspBeginRun returned no row. The run was not opened, so nothing this run does could be " +
                    "recorded against it.");
            }

            openedRunId = reader.GetInt64(reader.GetOrdinal("RunId"));
            actual = (CrawlMode)reader.GetByte(reader.GetOrdinal("Mode"));
            fullCrawlDue = reader.GetBoolean(reader.GetOrdinal("FullCrawlDue"));
            lastFullSuccessUtc = ReadUtc(reader, reader.GetOrdinal("LastFullSuccessUtc"));
            reaped = reader.GetInt32(reader.GetOrdinal("AbandonedRunsReaped"));
        }

        this.connectionId = connection.ConnectionId;
        this.runId = openedRunId;

        if (actual != requested)
        {
            this.log.Warning(
                "Crawl run {RunId} for connection {ConnectionId} was opened in {ActualMode} mode, not the requested " +
                "{RequestedMode}. crawl.uspBeginRun reports a full crawl is due (last full success " +
                "{LastFullSuccessUtc:o}, policy {FullEveryHours}h): an incremental read with no baseline to be a " +
                "delta against reads from the beginning of time anyway, so the run is recorded as what it will " +
                "actually do.",
                openedRunId,
                connection.ConnectionId,
                actual,
                requested,
                lastFullSuccessUtc,
                fullEveryHours);
        }

        if (reaped > 0)
        {
            this.log.Warning(
                "Closed {AbandonedRuns} abandoned run(s) for connection {ConnectionId} on the way in. Previous " +
                "processes died without reporting; this run inherited the backlog rather than caused it.",
                reaped,
                connection.ConnectionId);
        }

        this.log.Information(
            "Crawl run {RunId} opened for connection {ConnectionId} in {Mode} mode{DryRun}.",
            openedRunId,
            connection.ConnectionId,
            actual,
            dryRun ? " (dry run)" : string.Empty);

        return new CrawlRunStart(openedRunId, actual, fullCrawlDue, lastFullSuccessUtc, reaped);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// uspGetItemState GUARANTEES that every row it returns is live, and there
    /// is deliberately no filter here to match. CrawlItemState has nowhere to
    /// carry State, so the guarantee has to be the procedure's - and duplicating
    /// it in C# would mean two filters that can disagree, with this one winning
    /// silently. If a tombstoned item ever reaches this dictionary, the fix
    /// belongs in sql/23.
    ///
    /// What the guarantee buys: a tombstoned or pending-delete item still has
    /// hashes that match the source, so returning one would have the engine
    /// conclude "unchanged", skip the write, and leave the item out of the index
    /// for good. Absent instead means new, new means write, and uspRecordWritten
    /// sets State back to 1 - which is how a resurrected item comes back.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, CrawlItemState>> GetItemStatesAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        (string id, _) = this.RequireOpenRun(nameof(this.GetItemStatesAsync));

        var known = new Dictionary<string, CrawlItemState>(StringComparer.OrdinalIgnoreCase);

        if (itemIds is null || !AnyUsable(itemIds))
        {
            return known;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspGetItemState");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(TableValued("@Items", "crawl.ItemIdList", ItemIdRows(itemIds)));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        int itemIdColumn = reader.GetOrdinal("ItemId");
        int itemTypeColumn = reader.GetOrdinal("ItemType");
        int contentHashColumn = reader.GetOrdinal("ContentHash");
        int aclHashColumn = reader.GetOrdinal("AclHash");
        int contentBytesColumn = reader.GetOrdinal("ContentBytes");
        int streakColumn = reader.GetOrdinal("UnchangedStreak");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = reader.GetString(itemIdColumn);

            known[key] = new CrawlItemState(
                key,
                reader.GetString(itemTypeColumn),
                reader.GetFieldValue<byte[]>(contentHashColumn),
                reader.GetFieldValue<byte[]>(aclHashColumn),
                reader.GetInt32(contentBytesColumn),
                reader.GetInt32(streakColumn));
        }

        return known;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One round trip for the whole batch, on a connection opened for this call
    /// alone. Several writer threads reach this method at once and each gets its
    /// own pooled connection rather than waiting behind a shared one.
    /// </remarks>
    public async Task RecordWrittenAsync(
        IReadOnlyCollection<CrawlItemState> items,
        CancellationToken cancellationToken)
    {
        (string id, long run) = this.RequireOpenRun(nameof(this.RecordWrittenAsync));

        if (items is null || !AnyUsable(items.Select(item => item.ItemId)))
        {
            return;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspRecordWritten");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        command.Parameters.Add(TableValued("@Items", "crawl.ItemStateList", ItemStateRows(items)));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cheap and mandatory. An unchanged item is not written to Graph, but it
    /// must still be marked seen or the sweep concludes the source stopped
    /// returning it. The batching here is the same as the written path's for the
    /// same reason: on a corpus that barely changes, this is the call that
    /// happens for nearly every item of nearly every run.
    /// </remarks>
    public async Task RecordUnchangedAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        (string id, long run) = this.RequireOpenRun(nameof(this.RecordUnchangedAsync));

        if (itemIds is null || !AnyUsable(itemIds))
        {
            return;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspRecordUnchanged");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        command.Parameters.Add(TableValued("@Items", "crawl.ItemIdList", ItemIdRows(itemIds)));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetLiveItemIdsAsync(CancellationToken cancellationToken)
    {
        (string id, _) = this.RequireOpenRun(nameof(this.GetLiveItemIdsAsync));

        var ids = new List<string>();

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspListLiveItemIds");

        command.Parameters.Add(Text("@ConnectionId", id, 64));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        int itemIdColumn = reader.GetOrdinal("ItemId");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(itemIdColumn));
        }

        return ids;
    }
    /// <inheritdoc/>
    /// <remarks>
    /// THE GUARD IS RETHROWN, NOT SWALLOWED AND NOT REWORDED. uspGetPendingDeletes
    /// raises 50007 with a message that names the counts - how many items of how
    /// many, what percentage, against what limit - and those counts are the whole
    /// value of the refusal: they are what tells an operator whether this is a
    /// real mass deletion or a source that returned too few rows. So the server's
    /// message is carried through verbatim inside an InvalidOperationException,
    /// which is what a caller can catch without referencing SqlClient.
    ///
    /// Error 50006 - the sweep refused because the run is incremental - is left
    /// as the SqlException it is. It is a programming error in the caller rather
    /// than a decision an operator has to make, and it must not be mistaken for
    /// the guard.
    /// </remarks>
    public async Task<IReadOnlyList<CrawlDeletion>> GetPendingDeletesAsync(
        double maxDeletePercent,
        bool overrideGuard,
        CancellationToken cancellationToken)
    {
        (string id, long run) = this.RequireOpenRun(nameof(this.GetPendingDeletesAsync));

        var deletions = new List<CrawlDeletion>();

        try
        {
            await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlCommand command = Procedure(sql, "crawl.uspGetPendingDeletes");

            command.Parameters.Add(Text("@ConnectionId", id, 64));
            command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
            command.Parameters.Add(new SqlParameter("@MaxDeletePercent", SqlDbType.Decimal)
            {
                Precision = 5,
                Scale = 2,
                Value = GuardPercent(maxDeletePercent),
            });
            command.Parameters.Add(new SqlParameter("@OverrideGuard", SqlDbType.Bit) { Value = overrideGuard });

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            int itemIdColumn = reader.GetOrdinal("ItemId");
            int itemTypeColumn = reader.GetOrdinal("ItemType");

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deletions.Add(new CrawlDeletion(
                    reader.GetString(itemIdColumn),
                    reader.GetString(itemTypeColumn)));
            }
        }
        catch (SqlException ex) when (ex.Number == DeleteGuardErrorNumber)
        {
            this.log.Error(
                "Delete sweep refused for connection {ConnectionId} on run {RunId}. {ServerMessage}",
                id,
                run,
                ex.Message);

            throw new InvalidOperationException(ex.Message, ex);
        }

        if (overrideGuard && deletions.Count > 0)
        {
            this.log.Warning(
                "Delete sweep for connection {ConnectionId} ran with the percentage guard overridden and proposes " +
                "{DeleteCount} removal(s). An override is an operator decision, never a retry.",
                id,
                deletions.Count);
        }

        return deletions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Includes the 404s, because an item Graph says is not there is an item that
    /// is not there. Reporting those as failures would leave them pending for
    /// ever and every later sweep would propose them again.
    /// </remarks>
    public async Task ConfirmDeletesAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        (string id, long run) = this.RequireOpenRun(nameof(this.ConfirmDeletesAsync));

        if (itemIds is null || !AnyUsable(itemIds))
        {
            return;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspConfirmDeletes");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        command.Parameters.Add(TableValued("@Items", "crawl.ItemIdList", ItemIdRows(itemIds)));

        object? confirmed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        this.log.Debug(
            "Tombstoned {Confirmed} of {Proposed} confirmed deletion(s) for connection {ConnectionId}.",
            confirmed ?? 0,
            itemIds.Count,
            id);
    }

    /// <inheritdoc/>
    public async Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken)
    {
        (string id, _) = this.RequireOpenRun(nameof(this.GetCheckpointAsync));

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await this.ReadCheckpointAsync(sql, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The marker is truncated to whole milliseconds before it is sent, DOWNWARDS.
    /// Checkpoint.MarkerTime is DATETIME2(3) and SQL Server rounds to the nearest
    /// millisecond on the way in, so a marker at .0005 would be stored as .001 -
    /// and the next run, which resumes strictly after the marker, would never see
    /// a row modified at .0007. Rounding down costs a re-read of rows already
    /// written, which the upsert makes free. Rounding up costs a row that is
    /// never indexed and never reported.
    /// </remarks>
    public async Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken)
    {
        (string id, long run) = this.RequireOpenRun(nameof(this.SaveCheckpointAsync));

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspSaveCheckpoint");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        command.Parameters.Add(new SqlParameter("@MarkerTime", SqlDbType.DateTime2)
        {
            Scale = 3,
            Value = FloorToMillisecond(marker.MarkerTime),
        });
        command.Parameters.Add(Text(
            "@MarkerKey",
            Fits(marker.MarkerKey ?? string.Empty, MarkerKeyWidth, "Checkpoint.MarkerKey"),
            MarkerKeyWidth));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The procedure refuses to move the marker backwards, so what comes
            // back is what is actually stored rather than what was asked for.
            // Logging the stored value is what makes an overlapping run visible:
            // the slower of two runs sees its own marker rejected here rather
            // than silently resetting the faster one's progress.
            DateTime? stored = ReadUtc(reader, reader.GetOrdinal("MarkerTime"));

            this.log.Debug(
                "Checkpoint for connection {ConnectionId} now stands at {MarkerTime:o} after {RunCount} run(s).",
                id,
                stored,
                reader.GetInt32(reader.GetOrdinal("RunCount")));
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Keys go through crawl.PrincipalKeyList, which is NVARCHAR(256) - the same
    /// width as PrincipalMap.SourceKey, so anything that can be cached can be
    /// looked up. That symmetry is the point of the type existing: through the
    /// 128-character ItemIdList a long principal could be stored at full length
    /// and never found again.
    ///
    /// A key past 256 characters is reported as a miss rather than truncated or
    /// thrown on, which is the same answer CachePrincipalAsync gives when asked
    /// to store one. Truncating would not miss - it would match a DIFFERENT
    /// principal's row and stamp an item with the wrong group - and throwing
    /// would end a crawl over an ACL entry that resolving live handles perfectly
    /// well. A miss costs one directory lookup per run.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
        string sourceType,
        IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        (string id, _) = this.RequireOpenRun(nameof(this.ResolvePrincipalsAsync));

        var resolved = new Dictionary<string, PrincipalGrant>(StringComparer.OrdinalIgnoreCase);

        if (sourceKeys is null || sourceKeys.Count == 0)
        {
            return resolved;
        }

        var askable = new List<string>(sourceKeys.Count);
        int tooLong = 0;

        foreach (string key in sourceKeys)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (key.Length > SourceKeyWidth)
            {
                tooLong++;
                continue;
            }

            askable.Add(key);
        }

        if (tooLong > 0)
        {
            this.log.Warning(
                "{TooLong} principal key(s) exceed PrincipalMap.SourceKey ({Width} characters), so they can be " +
                "neither cached nor looked up. They are reported as cache misses and the caller resolves them " +
                "against the directory, as it does for any principal it has not seen.",
                tooLong,
                SourceKeyWidth);
        }

        if (askable.Count == 0)
        {
            return resolved;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspResolvePrincipals");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(Text("@SourceType", Fits(sourceType, SourceTypeWidth, "sourceType"), SourceTypeWidth));
        command.Parameters.Add(TableValued("@Principals", "crawl.PrincipalKeyList", PrincipalKeyRows(askable)));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        int keyColumn = reader.GetOrdinal("SourceKey");
        int objectIdColumn = reader.GetOrdinal("EntraObjectId");
        int typeColumn = reader.GetOrdinal("EntraType");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = reader.GetString(keyColumn);

            resolved[key] = new PrincipalGrant(
                key,
                reader.IsDBNull(objectIdColumn) ? null : reader.GetGuid(objectIdColumn),
                reader.IsDBNull(typeColumn) ? null : reader.GetString(typeColumn));
        }

        return resolved;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A null object ID is stored rather than discarded. A source principal with
    /// no Entra counterpart, looked up on every item of every run for ever, is
    /// the single most expensive thing an unbounded resolver does, and the answer
    /// "there is nothing" is a real answer with a real TTL.
    ///
    /// The TTL is rounded UP to at least one minute. uspCachePrincipal takes
    /// whole minutes, so a shorter TimeSpan would truncate to zero and store an
    /// entry that has already expired - which is not a short cache, it is a cache
    /// that silently never hits.
    /// </remarks>
    public async Task CachePrincipalAsync(
        PrincipalGrant grant,
        string sourceType,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        (string id, _) = this.RequireOpenRun(nameof(this.CachePrincipalAsync));

        if (string.IsNullOrEmpty(grant.SourceKey))
        {
            return;
        }

        if (grant.SourceKey.Length > SourceKeyWidth)
        {
            this.log.Warning(
                "Principal key of {Length} characters exceeds PrincipalMap.SourceKey ({Width}). It is not cached; " +
                "the caller will resolve it again next run.",
                grant.SourceKey.Length,
                SourceKeyWidth);

            return;
        }

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Procedure(sql, "crawl.uspCachePrincipal");

        command.Parameters.Add(Text("@ConnectionId", id, 64));
        command.Parameters.Add(Text("@SourceType", Fits(sourceType, SourceTypeWidth, "sourceType"), SourceTypeWidth));
        command.Parameters.Add(Text("@SourceKey", grant.SourceKey, SourceKeyWidth));
        command.Parameters.Add(Optional("@EntraObjectId", SqlDbType.UniqueIdentifier, grant.EntraObjectId));
        command.Parameters.Add(Optional("@EntraType", SqlDbType.NVarChar, DirectoryType(grant.EntraType), 16));
        command.Parameters.Add(new SqlParameter("@TtlMinutes", SqlDbType.Int) { Value = TtlMinutes(ttl) });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An enqueue and nothing else. Writing this when it happens would put a
    /// database round trip inside the write loop's catch block, on precisely the
    /// run that is already struggling and on the path that is about to sleep
    /// anyway. There is no connection, no await and no lock here on purpose:
    /// several writer threads reach this at once during a throttling episode,
    /// which is exactly what ConcurrentQueue is for.
    /// </remarks>
    public void RecordThrottle(ThrottleEvent throttle)
    {
        if (Interlocked.Increment(ref this.throttlesBuffered) > ThrottleBufferLimit)
        {
            // Counted, not kept. See ThrottleBufferLimit: the run producing the
            // most of these is the one that can least afford an unbounded list.
            Interlocked.Increment(ref this.throttlesDropped);
            return;
        }

        this.throttles.Enqueue(throttle);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// FOUR CALLS, AND THE ORDER IS THE POINT. The per-type breakdown, then the
    /// timing table, then the buffered throttle events, and the run row LAST.
    /// sql/23 gives the reason: a dashboard reading a completed run must always
    /// find its detail present, and closing the run first would leave a window -
    /// short, but exactly the window a monitoring poll lands in - where a run
    /// reads as finished and its detail page is empty.
    ///
    /// The three detail calls are best-effort, and only the close is allowed to
    /// throw. A run left open because its timing table would not write is worse
    /// than a run closed without one: the first is reported as still running
    /// until the reaper closes it twelve hours later, the second is a complete
    /// run with a logged gap. Nothing is swallowed silently - each failure is
    /// logged with the call that produced it.
    /// </remarks>
    public async Task CompleteRunAsync(
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken)
    {
        (_, long run) = this.RequireOpenRun(nameof(this.CompleteRunAsync));

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await this.WriteRunDetailAsync(sql, run, byType, timing, cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = Procedure(sql, "crawl.uspCompleteRun");

        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        AddTotals(command, totals);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref this.runClosed, 1);

        this.log.Information("Crawl run {RunId} closed as succeeded.", run);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same four calls in the same order, and the counters are recorded here
    /// too. A run that died after nine hundred of a thousand items wrote nine
    /// hundred items, and a failure row full of zeroes invites the reader to
    /// conclude nothing happened.
    ///
    /// The error message is truncated to what the column holds rather than
    /// allowed to fail the call. A failure that could not be recorded because its
    /// own description was too long is the worst possible trade: the run would
    /// stay open and the reason would be lost.
    /// </remarks>
    public async Task FailRunAsync(
        string errorKind,
        string errorMessage,
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken)
    {
        (_, long run) = this.RequireOpenRun(nameof(this.FailRunAsync));

        await using SqlConnection sql = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await this.WriteRunDetailAsync(sql, run, byType, timing, cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = Procedure(sql, "crawl.uspFailRun");

        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
        command.Parameters.Add(Text("@ErrorKind", Truncate(errorKind, ErrorKindWidth), ErrorKindWidth));
        command.Parameters.Add(Text("@ErrorMessage", Truncate(errorMessage, ErrorMessageWidth), ErrorMessageWidth));
        AddTotals(command, totals);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref this.runClosed, 1);

        this.log.Information("Crawl run {RunId} closed as failed: {ErrorKind}.", run, errorKind);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Holds nothing that needs releasing - every connection is opened and closed
    /// inside the call that used it - so this only reports. A run still open here
    /// is a run that will sit in status 1 until a later run reaps it, and saying
    /// so at the moment the process is ending is the only chance anyone has to
    /// connect the two.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (this.connectionId is not null && Volatile.Read(ref this.runClosed) == 0)
        {
            this.log.Warning(
                "Crawl run {RunId} for connection {ConnectionId} was never closed. It stays in status 1 until a " +
                "later run reaps it as abandoned, and {Buffered} buffered throttle event(s) are discarded with it.",
                this.runId,
                this.connectionId,
                this.throttles.Count);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Writes the run's per-type counters, its timing table and its throttle events.</summary>
    /// <param name="sql">The connection the whole closing sequence runs on.</param>
    /// <param name="run">The run being closed.</param>
    /// <param name="byType">The per-kind counters, possibly empty.</param>
    /// <param name="timing">The timing attribution.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// Best-effort by design, and the reasoning is in the two callers' remarks.
    /// One connection carries all three because they run on one thread once the
    /// writers have stopped - the only place in this class where a connection is
    /// held across commands.
    /// </remarks>
    private async Task WriteRunDetailAsync(
        SqlConnection sql,
        long run,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken)
    {
        if (byType is not null && byType.Count > 0)
        {
            try
            {
                await using SqlCommand command = Procedure(sql, "crawl.uspRecordRunItemTypes");

                command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
                command.Parameters.Add(TableValued("@Counts", "crawl.ItemTypeCountList", ItemTypeRows(byType)));

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.log.Warning(
                    RedactedException.Wrap(ex),
                    "crawl.uspRecordRunItemTypes failed for run {RunId}. The run still closes; its detail page will " +
                    "show no per-type breakdown.",
                    run);
            }
        }

        if (timing is not null)
        {
            try
            {
                await using SqlCommand command = Procedure(sql, "crawl.uspSaveRunTiming");

                command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
                command.Parameters.Add(TableValued("@Phases", "crawl.PhaseTimingList", PhaseTimingRows(timing)));

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.log.Warning(
                    RedactedException.Wrap(ex),
                    "crawl.uspSaveRunTiming failed for run {RunId}. The run still closes; \"was that run " +
                    "throttle-bound\" stays a log hunt for this one.",
                    run);
            }
        }

        try
        {
            await this.FlushThrottlesAsync(sql, run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.log.Warning(
                RedactedException.Wrap(ex),
                "Flushing buffered throttle events failed for run {RunId}. The run still closes; " +
                "Run.ThrottleWaits still carries the count.",
                run);
        }
    }

    /// <summary>Drains the throttle buffer into crawl.uspRecordThrottles.</summary>
    /// <param name="sql">The connection the closing sequence runs on.</param>
    /// <param name="run">The run the events belong to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// One call for the whole buffer, through crawl.ThrottleEventList. The
    /// singular uspRecordThrottle still exists for a caller with one event and no
    /// buffer to flush; this is not that caller, and the run that produced the
    /// most of these is the one that can least afford a round trip per refusal.
    ///
    /// EACH EVENT CARRIES ITS OWN OccurredUtc. Letting the column default at
    /// flush time would stamp every event in the run with the same instant, which
    /// destroys the only thing the raw rows are kept for: whether the throttling
    /// was clustered in one bad minute or spread evenly across the hour. Those
    /// two readings argue for opposite changes.
    /// </remarks>
    private async Task FlushThrottlesAsync(SqlConnection sql, long run, CancellationToken cancellationToken)
    {
        var pending = new List<ThrottleEvent>(Math.Min(this.throttles.Count, ThrottleBufferLimit));

        while (this.throttles.TryDequeue(out ThrottleEvent throttle))
        {
            pending.Add(throttle);
        }

        if (pending.Count > 0)
        {
            await using SqlCommand command = Procedure(sql, "crawl.uspRecordThrottles");

            command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.BigInt) { Value = run });
            command.Parameters.Add(TableValued("@Events", "crawl.ThrottleEventList", ThrottleEventRows(pending)));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int written = pending.Count;
        int dropped = Volatile.Read(ref this.throttlesDropped);

        if (dropped > 0)
        {
            this.log.Warning(
                "Recorded {Written} throttle event(s) for run {RunId} and dropped {Dropped} beyond the " +
                "{Limit}-event buffer. Run.ThrottleWaits still counts them all.",
                written,
                run,
                dropped,
                ThrottleBufferLimit);
        }
        else if (written > 0)
        {
            this.log.Debug("Recorded {Written} throttle event(s) for run {RunId}.", written, run);
        }
    }

    /// <summary>Reads the connection's checkpoint.</summary>
    /// <param name="sql">An open connection.</param>
    /// <param name="id">The connection ID.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The marker, or null when there is no row or no marker in it.</returns>
    /// <remarks>
    /// A row whose MarkerTime is null is the same answer as no row at all - it is
    /// what uspResetCheckpoint leaves behind - and both mean a full read is
    /// required. Collapsing them here means the caller has one case to handle
    /// rather than two that mean the same thing.
    /// </remarks>
    private async Task<CrawlMarker?> ReadCheckpointAsync(
        SqlConnection sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Procedure(sql, "crawl.uspGetCheckpoint");

        command.Parameters.Add(Text("@ConnectionId", id, 64));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int timeColumn = reader.GetOrdinal("MarkerTime");
        int keyColumn = reader.GetOrdinal("MarkerKey");

        if (reader.IsDBNull(timeColumn) || reader.IsDBNull(keyColumn))
        {
            return null;
        }

        return new CrawlMarker(
            DateTime.SpecifyKind(reader.GetDateTime(timeColumn), DateTimeKind.Utc),
            reader.GetString(keyColumn));
    }

    /// <summary>Opens a connection to the state database.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An open connection the caller owns and disposes.</returns>
    /// <remarks>
    /// One per call. See the file header: SqlClient's pool is what makes this
    /// cheap, and not sharing is what makes it correct when several writer
    /// threads are recording at once.
    /// </remarks>
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (this.connections is not null)
        {
            return await this.connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var connection = new SqlConnection(this.connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns the connection and run this store is recording against.</summary>
    /// <param name="operation">The calling method, for the message.</param>
    /// <returns>The connection ID and run ID.</returns>
    /// <remarks>
    /// ICrawlStateStore names the connection in exactly one place -
    /// BeginRunAsync's argument - so every other method depends on an ordering
    /// the interface does not state. Failing loudly here is the alternative to
    /// inventing a connection ID or recording against run zero, either of which
    /// would write rows that look valid and belong to nothing.
    /// </remarks>
    private (string ConnectionId, long RunId) RequireOpenRun(string operation)
    {
        string? id = this.connectionId;

        if (id is null)
        {
            throw new InvalidOperationException(
                operation + " was called before BeginRunAsync. This store learns which connection and which run it " +
                "is recording against from that call, and every procedure in sql/23 requires both.");
        }

        return (id, this.runId);
    }

    /// <summary>Creates a command for a stored procedure with the store's timeout.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="name">The schema-qualified procedure name.</param>
    /// <returns>The command, ready for parameters.</returns>
    private static SqlCommand Procedure(SqlConnection connection, string name)
    {
        return new SqlCommand(name, connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = CommandTimeoutSeconds,
        };
    }

    /// <summary>Creates an NVARCHAR parameter of a declared width.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value. Null becomes an empty string.</param>
    /// <param name="width">The declared width, matching the procedure's signature.</param>
    /// <returns>The parameter.</returns>
    /// <remarks>
    /// The width is declared rather than inferred so that SqlClient sends one
    /// plan-cacheable shape per procedure. Inferred widths vary with the value
    /// and produce a new cached plan per length, which is a well-known way to
    /// fill a plan cache with a thousand copies of the same query.
    /// </remarks>
    private static SqlParameter Text(string name, string? value, int width)
    {
        return new SqlParameter(name, SqlDbType.NVarChar, width) { Value = value ?? string.Empty };
    }

    /// <summary>Creates a parameter that may be null.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="type">The SQL type.</param>
    /// <param name="value">The value, or null for DBNull.</param>
    /// <param name="width">The declared width for the string types.</param>
    /// <returns>The parameter.</returns>
    private static SqlParameter Optional(string name, SqlDbType type, object? value, int width = 0)
    {
        SqlParameter parameter = width > 0
            ? new SqlParameter(name, type, width)
            : new SqlParameter(name, type);

        parameter.Value = value ?? DBNull.Value;

        return parameter;
    }

    /// <summary>Creates a table-valued parameter over a streamed row source.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="typeName">The schema-qualified table type.</param>
    /// <param name="rows">The rows, enumerated once as SqlClient sends them.</param>
    /// <returns>The parameter.</returns>
    /// <remarks>
    /// SqlClient throws on an empty enumeration, so every caller checks for an
    /// empty batch and returns before reaching this. That check is not a
    /// micro-optimisation: an empty batch is a normal outcome of a run where
    /// nothing changed, and it must cost nothing rather than throw.
    /// </remarks>
    private static SqlParameter TableValued(string name, string typeName, IEnumerable<SqlDataRecord> rows)
    {
        return new SqlParameter(name, SqlDbType.Structured)
        {
            TypeName = typeName,
            Value = rows,
        };
    }

    /// <summary>Adds the run counters shared by uspCompleteRun and uspFailRun.</summary>
    /// <param name="command">The command to add to.</param>
    /// <param name="totals">The counters.</param>
    /// <remarks>
    /// Every counter goes to both procedures, ItemsFailed included. A successful
    /// run can still have failed items - on the batching path one refused item
    /// does not stop the other nineteen - so a run that wrote 1,117 of 1,118 is a
    /// success with a number attached, and the number belongs on the row.
    /// </remarks>
    private static void AddTotals(SqlCommand command, RunTotals totals)
    {
        command.Parameters.Add(new SqlParameter("@ItemsRead", SqlDbType.Int) { Value = totals.ItemsRead });
        command.Parameters.Add(new SqlParameter("@ItemsWritten", SqlDbType.Int) { Value = totals.ItemsWritten });
        command.Parameters.Add(new SqlParameter("@ItemsUnchanged", SqlDbType.Int) { Value = totals.ItemsUnchanged });
        command.Parameters.Add(new SqlParameter("@ItemsDeleted", SqlDbType.Int) { Value = totals.ItemsDeleted });
        command.Parameters.Add(new SqlParameter("@ItemsSkipped", SqlDbType.Int) { Value = totals.ItemsSkipped });
        command.Parameters.Add(new SqlParameter("@ItemsDuplicate", SqlDbType.Int) { Value = totals.ItemsDuplicate });
        command.Parameters.Add(new SqlParameter("@ThrottleWaits", SqlDbType.Int) { Value = totals.ThrottleWaits });
        command.Parameters.Add(new SqlParameter("@BatchesSent", SqlDbType.Int) { Value = totals.BatchesSent });
        command.Parameters.Add(new SqlParameter("@BytesWritten", SqlDbType.BigInt) { Value = totals.BytesWritten });
        command.Parameters.Add(new SqlParameter("@ItemsFailed", SqlDbType.Int) { Value = totals.ItemsFailed });
    }

    /// <summary>Streams item IDs as crawl.ItemIdList rows.</summary>
    /// <param name="itemIds">The IDs.</param>
    /// <returns>One row per distinct ID.</returns>
    /// <remarks>
    /// One SqlDataRecord is filled and re-yielded rather than one allocated per
    /// row: SqlClient reads each record before asking for the next, which is what
    /// makes a hundred-thousand-item batch cost one row of memory instead of a
    /// second copy of the batch.
    ///
    /// Duplicates are dropped because ItemIdList has a clustered primary key on
    /// ItemId - a repeated ID does not overwrite, it fails the whole batch. The
    /// comparison is case-insensitive to match a default SQL Server collation, so
    /// what is rejected here is what the server would have rejected.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> ItemIdRows(IEnumerable<string> itemIds)
    {
        var columns = new[] { new SqlMetaData("ItemId", SqlDbType.NVarChar, ItemIdWidth) };
        var record = new SqlDataRecord(columns);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string itemId in itemIds)
        {
            if (string.IsNullOrEmpty(itemId) || !seen.Add(itemId))
            {
                continue;
            }

            record.SetString(0, Fits(itemId, ItemIdWidth, "ItemId"));

            yield return record;
        }
    }

    /// <summary>Reports whether a batch holds at least one row worth sending.</summary>
    /// <param name="values">The keys or IDs about to be streamed.</param>
    /// <returns>True when at least one is non-empty.</returns>
    /// <remarks>
    /// SqlClient throws on a table-valued parameter whose enumeration produces no
    /// rows, and the streamers below skip empty strings - so a batch of nothing
    /// but empty IDs would reach the server as an empty TVP and fail the call
    /// with "there are no records in the SqlDataRecord enumeration", which names
    /// neither the batch nor the reason. Counting the collection is not enough to
    /// rule that out; this is.
    /// </remarks>
    private static bool AnyUsable(IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Streams source principal keys as crawl.PrincipalKeyList rows.</summary>
    /// <param name="sourceKeys">The keys.</param>
    /// <returns>One row per distinct key.</returns>
    /// <remarks>
    /// A separate type from ItemIdList and not interchangeable with it, which is
    /// the whole reason sql/20 defines both. An item ID is capped at 128
    /// characters by Graph; a source principal is not, and an Active Directory
    /// distinguished name routinely runs past it. Sending principals through the
    /// narrower type would let one be cached at full length and never looked up
    /// again - or truncated into a match against a different principal's row,
    /// which stamps an item with the wrong group.
    ///
    /// The Fits call cannot fire from the one caller here, which filters
    /// over-long keys out and warns about them. It stays as the invariant a
    /// second caller would have to satisfy, so the failure would be an
    /// exception naming the column rather than a silent truncation.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> PrincipalKeyRows(IEnumerable<string> sourceKeys)
    {
        var columns = new[] { new SqlMetaData("SourceKey", SqlDbType.NVarChar, SourceKeyWidth) };
        var record = new SqlDataRecord(columns);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string sourceKey in sourceKeys)
        {
            if (string.IsNullOrEmpty(sourceKey) || !seen.Add(sourceKey))
            {
                continue;
            }

            record.SetString(0, Fits(sourceKey, SourceKeyWidth, "SourceKey"));

            yield return record;
        }
    }

    /// <summary>Streams buffered refusals as crawl.ThrottleEventList rows.</summary>
    /// <param name="events">The events, in the order they were recorded.</param>
    /// <returns>One row per event.</returns>
    /// <remarks>
    /// No de-duplication, unlike every other streamer here: ThrottleEventList has
    /// no primary key because two identical refusals a second apart are two
    /// refusals, and collapsing them would understate exactly the run this table
    /// exists to describe.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> ThrottleEventRows(IEnumerable<ThrottleEvent> events)
    {
        var columns = new[]
        {
            new SqlMetaData("OccurredUtc", SqlDbType.DateTime2, 0, 3),
            new SqlMetaData("StatusCode", SqlDbType.Int),
            new SqlMetaData("RetryAfterSeconds", SqlDbType.Int),
            new SqlMetaData("Endpoint", SqlDbType.NVarChar, EndpointWidth),
            new SqlMetaData("AttemptNumber", SqlDbType.Int),
        };

        var record = new SqlDataRecord(columns);

        foreach (ThrottleEvent throttle in events)
        {
            record.SetDateTime(0, FloorToMillisecond(throttle.OccurredUtc));
            record.SetInt32(1, throttle.StatusCode);

            if (throttle.RetryAfterSeconds is int retryAfter)
            {
                record.SetInt32(2, retryAfter);
            }
            else
            {
                record.SetDBNull(2);
            }

            record.SetString(
                3,
                Truncate(string.IsNullOrEmpty(throttle.Endpoint) ? "item" : throttle.Endpoint, EndpointWidth));
            record.SetInt32(4, throttle.AttemptNumber);

            yield return record;
        }
    }

    /// <summary>Streams item state as crawl.ItemStateList rows.</summary>
    /// <param name="items">The items, as written.</param>
    /// <returns>One row per distinct item.</returns>
    /// <remarks>
    /// The hashes are checked for length before they are sent. A BINARY(32)
    /// column pads a short array with zeroes rather than refusing it, so a hash
    /// of the wrong size would be stored as a value that no future hash of the
    /// same item can ever equal - the item would be rewritten every run for ever,
    /// and nothing would report it as wrong.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> ItemStateRows(IEnumerable<CrawlItemState> items)
    {
        var columns = new[]
        {
            new SqlMetaData("ItemId", SqlDbType.NVarChar, ItemIdWidth),
            new SqlMetaData("ItemType", SqlDbType.NVarChar, ItemTypeWidth),
            new SqlMetaData("ContentHash", SqlDbType.Binary, ItemHasher.HashBytes),
            new SqlMetaData("AclHash", SqlDbType.Binary, ItemHasher.HashBytes),
            new SqlMetaData("ContentBytes", SqlDbType.Int),
        };

        var record = new SqlDataRecord(columns);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CrawlItemState item in items)
        {
            if (string.IsNullOrEmpty(item.ItemId) || !seen.Add(item.ItemId))
            {
                continue;
            }

            record.SetString(0, Fits(item.ItemId, ItemIdWidth, "ItemId"));
            record.SetString(1, Fits(item.ItemType ?? string.Empty, ItemTypeWidth, "ItemType"));
            record.SetValue(2, Hash(item.ContentHash, item.ItemId, nameof(CrawlItemState.ContentHash)));
            record.SetValue(3, Hash(item.AclHash, item.ItemId, nameof(CrawlItemState.AclHash)));

            // CK_Item_Bytes refuses a negative size. Clamping rather than
            // throwing keeps a miscounted byte total from ending a run that
            // otherwise wrote every item correctly.
            record.SetInt32(4, item.ContentBytes < 0 ? 0 : item.ContentBytes);

            yield return record;
        }
    }

    /// <summary>Streams the per-type counters as crawl.ItemTypeCountList rows.</summary>
    /// <param name="byType">The counters.</param>
    /// <returns>One row per distinct item type.</returns>
    private static IEnumerable<SqlDataRecord> ItemTypeRows(IEnumerable<ItemTypeTotals> byType)
    {
        var columns = new[]
        {
            new SqlMetaData("ItemType", SqlDbType.NVarChar, ItemTypeWidth),
            new SqlMetaData("ItemsWritten", SqlDbType.Int),
            new SqlMetaData("ItemsUnchanged", SqlDbType.Int),
            new SqlMetaData("ItemsDeleted", SqlDbType.Int),
            new SqlMetaData("ItemsSkipped", SqlDbType.Int),
            new SqlMetaData("ItemsFailed", SqlDbType.Int),
            new SqlMetaData("BytesWritten", SqlDbType.BigInt),
        };

        var record = new SqlDataRecord(columns);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ItemTypeTotals totals in byType)
        {
            string itemType = totals.ItemType ?? string.Empty;

            if (!seen.Add(itemType))
            {
                continue;
            }

            record.SetString(0, Fits(itemType, ItemTypeWidth, "ItemType"));
            record.SetInt32(1, totals.ItemsWritten);
            record.SetInt32(2, totals.ItemsUnchanged);
            record.SetInt32(3, totals.ItemsDeleted);
            record.SetInt32(4, totals.ItemsSkipped);
            record.SetInt32(5, totals.ItemsFailed);
            record.SetInt64(6, totals.BytesWritten);

            yield return record;
        }
    }

    /// <summary>Streams PushTiming's seven series as crawl.PhaseTimingList rows.</summary>
    /// <param name="timing">The run's timing attribution.</param>
    /// <returns>Seven rows, in the order PushTiming.Report prints them.</returns>
    /// <remarks>
    /// The phase names are the property names rather than the display labels
    /// PushTiming uses in its report ("source read", "ROW TOTAL"), because these
    /// go into a primary key that a dashboard query filters on. A label that
    /// exists to be read by a person is not a key.
    ///
    /// ContentBytes IS NOT A DURATION, and Unit is what says so. PushTiming
    /// holds one series for content size alongside its six timing series -
    /// it belongs here because a body near the 3.5 MB cap explains a slow PUT on
    /// its own - and its percentiles land in columns named Microseconds. The unit
    /// travels with the row rather than living in a reader's head, because a
    /// convention that has to be remembered is one that eventually is not.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> PhaseTimingRows(PushTiming timing)
    {
        var columns = new[]
        {
            new SqlMetaData("Phase", SqlDbType.NVarChar, 32),
            new SqlMetaData("Unit", SqlDbType.NVarChar, 16),
            new SqlMetaData("SampleCount", SqlDbType.BigInt),
            new SqlMetaData("TotalMicroseconds", SqlDbType.BigInt),
            new SqlMetaData("P50Microseconds", SqlDbType.BigInt),
            new SqlMetaData("P95Microseconds", SqlDbType.BigInt),
            new SqlMetaData("P99Microseconds", SqlDbType.BigInt),
            new SqlMetaData("MaxMicroseconds", SqlDbType.BigInt),
        };

        const string Microseconds = "microseconds";

        (string Phase, string Unit, TimingSeries Series)[] phases =
        {
            ("SourceRead", Microseconds, timing.SourceRead),
            ("Prepare", Microseconds, timing.Prepare),
            ("WriteInFlight", Microseconds, timing.WriteInFlight),
            ("WriteBackoff", Microseconds, timing.WriteBackoff),
            ("Commit", Microseconds, timing.Commit),
            ("RowTotal", Microseconds, timing.RowTotal),
            ("ContentBytes", "bytes", timing.ContentBytes),
        };

        var record = new SqlDataRecord(columns);

        foreach ((string phase, string unit, TimingSeries series) in phases)
        {
            record.SetString(0, phase);
            record.SetString(1, unit);
            record.SetInt64(2, series.Count);
            record.SetInt64(3, series.Sum);
            record.SetInt64(4, series.Percentile(0.50));
            record.SetInt64(5, series.Percentile(0.95));
            record.SetInt64(6, series.Percentile(0.99));
            record.SetInt64(7, series.Max);

            yield return record;
        }
    }

    /// <summary>Checks a value against the column that will hold it.</summary>
    /// <param name="value">The value.</param>
    /// <param name="width">The column's width.</param>
    /// <param name="column">The column, for the message.</param>
    /// <returns>The value, unchanged.</returns>
    /// <remarks>
    /// Throws rather than truncates, and only for the values that ARE identity -
    /// item IDs, item types, principal keys. A truncated identifier is not a
    /// shorter name for the same thing, it is a different thing: two items whose
    /// IDs differ only past the limit would collide on one inventory row and each
    /// would overwrite the other's hashes every run.
    /// </remarks>
    private static string Fits(string value, int width, string column)
    {
        if (value.Length > width)
        {
            throw new InvalidOperationException(
                $"{column} is {value.Length} characters; ConnectorState holds {width}. The value is deliberately " +
                "not logged. Truncating it would merge two distinct records onto one row, so this refuses instead.");
        }

        return value;
    }

    /// <summary>Shortens a value that is description rather than identity.</summary>
    /// <param name="value">The value.</param>
    /// <param name="width">The column's width.</param>
    /// <returns>The value, cut to the width.</returns>
    /// <remarks>
    /// The opposite trade from <see cref="Fits"/>, and it applies to error kinds
    /// and error messages only. Losing the tail of a failure description is a
    /// smaller loss than failing to record the failure at all.
    /// </remarks>
    private static string Truncate(string? value, int width)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= width ? value : value.Substring(0, width);
    }

    /// <summary>Checks a hash is the width the BINARY(32) columns declare.</summary>
    /// <param name="hash">The hash.</param>
    /// <param name="itemId">The item, for the message.</param>
    /// <param name="which">Which hash, for the message.</param>
    /// <returns>The hash, unchanged.</returns>
    private static byte[] Hash(byte[]? hash, string itemId, string which)
    {
        if (hash is null || hash.Length != ItemHasher.HashBytes)
        {
            throw new InvalidOperationException(
                $"{which} for item '{itemId}' is {hash?.Length ?? 0} bytes; ConnectorState stores " +
                $"BINARY({ItemHasher.HashBytes}). A short hash is zero-padded rather than refused by SQL Server, " +
                "which would make this item compare as changed on every run for ever.");
        }

        return hash;
    }

    /// <summary>Converts the guard percentage to what DECIMAL(5, 2) can hold.</summary>
    /// <param name="maxDeletePercent">The caller's percentage.</param>
    /// <returns>A value in [0, 100] with two decimal places.</returns>
    /// <remarks>
    /// Clamped rather than passed through. A percentage above 100 cannot describe
    /// a share of the corpus, and one above 999.99 would overflow the parameter -
    /// an overflow that would surface as a failed sweep rather than as the
    /// disabled guard the caller apparently meant. NaN clamps to zero, which is
    /// the strictest guard rather than the loosest.
    /// </remarks>
    private static decimal GuardPercent(double maxDeletePercent)
    {
        if (double.IsNaN(maxDeletePercent) || maxDeletePercent <= 0)
        {
            return 0m;
        }

        if (maxDeletePercent >= 100)
        {
            return 100m;
        }

        return Math.Round((decimal)maxDeletePercent, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Converts a TTL to the whole minutes uspCachePrincipal takes.</summary>
    /// <param name="ttl">The caller's TTL.</param>
    /// <returns>At least one minute.</returns>
    private static int TtlMinutes(TimeSpan ttl)
    {
        double minutes = Math.Ceiling(ttl.TotalMinutes);

        if (minutes < 1)
        {
            return 1;
        }

        return minutes >= int.MaxValue ? int.MaxValue : (int)minutes;
    }

    /// <summary>Normalises a directory type to what CK_PrincipalMap_Type allows.</summary>
    /// <param name="entraType">The type as the resolver reported it.</param>
    /// <returns>"group", "user", or null.</returns>
    /// <remarks>
    /// Anything else becomes null rather than failing the insert. The constraint
    /// exists to keep the column meaningful, and a value it does not recognise
    /// carries no meaning worth ending a run over - the object ID beside it is
    /// what the ACL is actually built from.
    /// </remarks>
    private static string? DirectoryType(string? entraType)
    {
        if (string.IsNullOrWhiteSpace(entraType))
        {
            return null;
        }

        string normalised = entraType.Trim().ToLowerInvariant();

        return normalised is "group" or "user" ? normalised : null;
    }

    /// <summary>Truncates a timestamp down to whole milliseconds, in UTC.</summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The same instant, never later, with a UTC kind.</returns>
    /// <remarks>
    /// DOWNWARDS, and for the checkpoint that is the whole point - see
    /// SaveCheckpointAsync's remarks: a marker rounded up skips rows, a marker
    /// rounded down re-reads them. Throttle timestamps go through the same
    /// helper, where the direction does not matter but the kind does: a local
    /// timestamp is converted rather than reinterpreted, and an unspecified one
    /// is taken as UTC, which is what every DATETIME2 column in sql/21 holds.
    /// </remarks>
    private static DateTime FloorToMillisecond(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>Reads a nullable DATETIME2 column as UTC.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The value with a UTC kind, or null.</returns>
    /// <remarks>
    /// SqlClient returns DATETIME2 with an unspecified kind, and an unspecified
    /// kind formatted or compared against DateTime.UtcNow silently acquires the
    /// server's local offset. Every such column in sql/21 is written by
    /// SYSUTCDATETIME(), so stamping the kind here is restating what is already
    /// true rather than converting.
    /// </remarks>
    private static DateTime? ReadUtc(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    /// <summary>Reports the tool version recorded against every run.</summary>
    /// <returns>The entry assembly's informational version, or "unknown".</returns>
    /// <remarks>
    /// Read from the entry assembly rather than this one, because what the run
    /// history has to answer is "which build of the push tool produced this
    /// row", and a shared library's version is not that.
    /// </remarks>
    private static string ToolVersion()
    {
        Assembly? entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        string? version = entry?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? entry?.GetName().Version?.ToString(3);

        return string.IsNullOrWhiteSpace(version)
            ? "unknown"
            : Truncate(version, 64);
    }
}
