// ---------------------------------------------------------------------------
// HierarchyPushConnector.cs
// The three level test case: Customer -> Engagement -> TimeEntry.
//
// This is the whole connector. Credentials, the vault, the SQL connection,
// connection and schema registration, truncation, ACLs, throttling, exit codes
// and logging are the engine's, in PushCore, identical for every source.
//
// WHAT THIS DEMONSTRATES
//
// A Graph external item has a flat property list. There is no parent property,
// no child collection and no join at retrieval time. Copilot fetches individual
// items; it does not traverse anything. So a hierarchy cannot be indexed as a
// hierarchy - it has to be flattened, with every descendant physically carrying
// its ancestors' text, or a search for the customer will never reach the time
// entries.
//
// That flattening lives in sql/12-timesheet-views.sql, deliberately: a DBA can
// read exactly what leaves the database, and this file holds one query against
// one view with no join logic at all.
//
// TWO READS, ONE ITEM. This connector has two queries, not one, and which it
// uses is decided by Settings:Incremental:
//
//   off - dbo.vwExternalItems, parents first, no marker, several writers. The
//         Tier 2 behaviour every run before this one had: read everything, let
//         the content hashes decide what is written.
//   on  - dbo.vwExternalItemsIncremental, ascending (marker, id), a marker on
//         every item, one writer. Tier 1: read only what changed.
//
// The item they build is IDENTICAL, column for column, and that is a
// requirement rather than a nicety. The engine escalates to a full crawl on a
// hash-version change and every Settings:FullEveryHours, so one connection
// alternates between the two reads for the rest of its life; if they disagreed
// about a single property, every alternation would rewrite the whole corpus and
// report an ordinary success. MapRow below is shared between them for exactly
// that reason, and sql/35 proves the two views agree on all thirty columns.
// ---------------------------------------------------------------------------

namespace SqlHierarchyPush;

using Connector.Security.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;
using PushCore.Sql;
using PushCore.State;

/// <summary>Customers, engagements and logged time, flattened by SQL views.</summary>
public sealed class HierarchyPushConnector : ISqlPushConnector
{
    /// <summary>
    /// The column an incremental read carries the checkpoint marker in.
    ///
    /// Not simply "EffectiveLastModified": the query wraps that column in the
    /// read ceiling described in <see cref="BuildIncrementalQuery"/>, so what
    /// arrives here is "the marker this item may advance the checkpoint to, or
    /// null if it may not". Giving the derived value its own name is what stops
    /// the two being confused at the reader.
    /// </summary>
    public const string MarkerColumn = "MarkerTime";

    /// <summary>
    /// The view an incremental read comes from, added by sql/26.
    ///
    /// It is <see cref="DefaultItemView"/> plus EffectiveLastModified - the
    /// hierarchy-aware timestamp - and nothing else. Both timestamps are
    /// projected under their own names, because they answer different questions
    /// and the whole feature turns on not confusing them.
    /// </summary>
    public const string IncrementalItemView = "dbo.vwExternalItemsIncremental";

    /// <summary>
    /// The thirty columns of the projection, named once.
    ///
    /// Shared by both queries so they cannot drift. A column added to one and
    /// not the other changes the item the connector builds on one kind of run
    /// and not the other, which makes every escalation to a full crawl rewrite
    /// the corpus - the single most expensive thing that can go wrong here, and
    /// the only symptom is a large bill.
    /// </summary>
    private const string Columns =
        "ItemId, ItemType, Title, Url, LastModified, HierarchyPath, ContainerName, ContainerUrl, " +
        "CustomerId, CustomerName, CustomerCode, Industry, Region, AccountManager, " +
        "EngagementId, EngagementName, EngagementCode, Practice, Status, ProjectManager, " +
        "ConsultantName, ConsultantEmail, WorkDate, Hours, Billable, WorkType, " +
        "ContractValue, TotalHours, ChildCount, Content";

    /// <inheritdoc/>
    public string Key => "consultingwork";

    /// <inheritdoc/>
    public string DisplayName => "Customers, engagements and logged time";

    /// <inheritdoc/>
    public string DefaultConnectionId => "consultingwork";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Consulting work";

    /// <inheritdoc/>
    public string DefaultDescription => "Customers, engagements and logged time";

    /// <inheritdoc/>
    public string DefaultItemView => "dbo.vwExternalItems";

    /// <summary>
    /// One flat schema serves all three levels. A time entry leaves the
    /// engagement and customer columns populated; a customer leaves the
    /// descendant columns unset. That is what "flat" costs, and it is cheaper
    /// than three connections, which could not be searched as one thing.
    ///
    /// Two platform rules shape every line, both enforced by PushSchema.Prop:
    ///   * isSearchable and isRefinable are mutually exclusive. Anything a
    ///     person types goes in the searchable column; anything they filter or
    ///     facet by goes in the refinable one.
    ///   * property names are 32 alphanumeric characters at most.
    /// </summary>
    /// <returns>The 26 property schema.</returns>
    public Schema BuildSchema()
    {
        return PushSchema.Of(
            // --- which level this item is, and where it sits --------------------
            // Refinable, not searchable: you facet by it, you do not type it.
            PushSchema.Prop("itemType", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            PushSchema.Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.Title),
            PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
            PushSchema.Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                label: Label.LastModifiedDateTime),

            // containerName and containerUrl are how the platform expresses
            // "this item sits inside that one" - an engagement's container is its
            // customer, a time entry's is its engagement. It is the closest a
            // flat index gets to the hierarchy, and result surfaces show it.
            PushSchema.Prop("containerName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true, label: Label.ContainerName),
            PushSchema.Prop("containerUrl", PropertyType.String, retrievable: true, label: Label.ContainerUrl),

            // The breadcrumb as one searchable string: "Contoso > Data Platform
            // Migration > 2026-08-14 Priya Raman". Matches a query that names two
            // levels at once, which neither level alone would.
            PushSchema.Prop("hierarchyPath", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),

            // --- level 1, present on ALL THREE levels ---------------------------
            // This block is the requirement. customerName is searchable on the
            // time entry as well as on the customer, which is the only reason a
            // search for the customer reaches the time entry at all.
            PushSchema.Prop("customerName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("customerCode", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("accountManager", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("industry", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("region", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 2, present on engagements and time entries ---------------
            PushSchema.Prop("engagementName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("engagementCode", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("projectManager", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("practice", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 3 --------------------------------------------------------
            PushSchema.Prop("consultantName", PropertyType.String, searchable: true, queryable: true,
                retrievable: true),
            PushSchema.Prop("consultantEmail", PropertyType.String, queryable: true, retrievable: true),
            PushSchema.Prop("workType", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("workDate", PropertyType.DateTime, queryable: true, retrievable: true),
            PushSchema.Prop("hours", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("billable", PropertyType.Boolean, queryable: true, retrievable: true),

            // --- roll ups, so an answer can cite a number without arithmetic ----
            PushSchema.Prop("contractValue", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("totalHours", PropertyType.Double, queryable: true, retrievable: true),
            PushSchema.Prop("childCount", PropertyType.Int64, queryable: true, retrievable: true));
    }

    /// <summary>
    /// Parents first. A run interrupted halfway then leaves customers and
    /// engagements present with some time entries missing, which is a coherent
    /// index; the reverse would leave orphaned children whose ancestors are not
    /// there to be found.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The query against the flattening view.</returns>
    /// <remarks>
    /// This is the FULL read, and it keeps a position nowhere. That is why it
    /// may be ordered for the convenience of an interrupted run rather than for
    /// a checkpoint, and why <see cref="SqlPushSource"/> may write its items
    /// several at a time. An incremental read cannot have either - see
    /// <see cref="BuildIncrementalQuery"/>.
    /// </remarks>
    public string BuildQuery(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The view name is validated as an identifier in SourceSection.Validate,
        // which is what makes concatenating it safe.
        string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

        return
            $"SELECT {top}{Columns} " +
            $"FROM {options.Source.ItemView} " +
            "ORDER BY CASE ItemType WHEN 'Customer' THEN 0 WHEN 'Engagement' THEN 1 ELSE 2 END, ItemId;";
    }

    /// <summary>
    /// The same thirty columns, from the marker-bearing view, in the one order a
    /// checkpoint can be taken from, and starting strictly after the marker when
    /// there is one.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <param name="resumeFrom">
    /// Where the previous run stopped, or null for a full read. Null is the
    /// normal state of a connection that has never checkpointed and is also what
    /// the engine hands over whenever it escalated this run to a full crawl.
    /// </param>
    /// <returns>The T-SQL, expecting @ResumeTime and @ResumeKey when resuming.</returns>
    /// <remarks>
    /// <para>THE ORDERING. Ascending (EffectiveLastModified, ItemId), strictly,
    /// and it is the only thing here with no room for judgement. The checkpoint
    /// is forward-only - uspSaveCheckpoint refuses to move it backwards, and the
    /// engine freezes it for the rest of a run once any item is refused - so an
    /// out-of-order read does not produce an out-of-order checkpoint. It
    /// produces a checkpoint sitting at the largest pair the run happened to
    /// reach, with rows below that pair that were never written. The next run
    /// starts strictly after the marker and never sees them again. Nothing
    /// reports it, the run is fast, and the corpus is wrong. Note what is given
    /// up for it: BuildQuery's parents-first ordering, which exists so an
    /// interrupted run leaves no orphaned children. A checkpoint is a strictly
    /// better answer to the same worry - an interrupted run resumes where it
    /// stopped - and in practice the loss is not even felt here, because the
    /// cascading triggers stamp a whole subtree with one timestamp and 'cust'
    /// sorts before 'eng' sorts before 'time' inside the tie.</para>
    ///
    /// <para>THE PREDICATE, AND TIES. "Strictly after the pair", not "after the
    /// timestamp": every item in this source shares its EffectiveLastModified
    /// with at least one other, because a trigger stamps an entire cascade with
    /// a single value - 111,900 of 111,900 items on the pilot corpus, in tie
    /// groups of up to 16,743. A timestamp-only marker would therefore either
    /// re-read a group of sixteen thousand for ever (with &gt;=) or lose whichever
    /// of them had not been written when the run stopped (with &gt;). The pair
    /// makes the order total, so a chunk boundary that falls INSIDE a tie group
    /// is safe in both directions: the checkpoint is (T, id-of-last-written), the
    /// next run returns every row with the same T and a larger id, and returns no
    /// row it already had. This is the case most likely to be silently wrong, and
    /// it is the one sql/35 check 3 exists to keep provable.</para>
    ///
    /// <para>THE READ CEILING. Both the filter and the marker are capped at
    /// SYSUTCDATETIME() as it stood when the query started, evaluated ONCE into a
    /// variable rather than per row. Without it, a row modified while the crawl
    /// is running can be stamped with a timestamp the crawl has already passed,
    /// and would then sit below a checkpoint that never covered it - skipped for
    /// ever. Rounding makes the comparison exact rather than merely careful: the
    /// source column is DATETIME2(3) and SYSUTCDATETIME() rounds to nearest on
    /// the way into a (3) variable, and rounding is monotonic, so anything
    /// modified at or after the start of the read is at or after the ceiling and
    /// a strict &lt; excludes it. It is read on the next run instead.</para>
    ///
    /// <para>WHY THE FILTER IS ONLY ON THE RESUME PATH. A full read must return
    /// every live record, because the delete sweep concludes that anything a
    /// completed full crawl did not return has been deleted from the source. A
    /// ceiling in the WHERE clause of a full read would hide rows modified during
    /// the crawl and the sweep would remove them from the index. So the full read
    /// returns them and merely declines to CHECKPOINT past them: the CASE gives
    /// them a null marker, they sort last because the order is ascending, and the
    /// engine's "last item that carried a marker" lands below the ceiling.
    /// Completeness for the sweep and a safe checkpoint, without trading either.</para>
    ///
    /// <para>OPTION (RECOMPILE) because the delta size varies by five orders of
    /// magnitude between the first run and the steady state - 111,900 rows and
    /// then nought to a handful - and a plan cached for either one is the wrong
    /// plan for the other. It is the cheapest possible fix for that: this query
    /// runs once per crawl.</para>
    /// </remarks>
    public static string BuildIncrementalQuery(PushOptions options, CrawlMarker? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(options);

        string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

        string where = resumeFrom is null
            ? string.Empty
            : "WHERE EffectiveLastModified < @ReadCeiling " +
              "AND (EffectiveLastModified > @ResumeTime " +
              "OR (EffectiveLastModified = @ResumeTime AND ItemId > @ResumeKey)) ";

        return
            "DECLARE @ReadCeiling DATETIME2(3) = SYSUTCDATETIME(); " +
            $"SELECT {top}{Columns}, " +
            $"CASE WHEN EffectiveLastModified < @ReadCeiling THEN EffectiveLastModified END AS {MarkerColumn} " +
            $"FROM {options.Source.ItemView} " +
            where +
            "ORDER BY EffectiveLastModified, ItemId " +
            "OPTION (RECOMPILE);";
    }

    /// <summary>
    /// Whether this run reads incrementally, which decides both which view is
    /// read and which source class reads it.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>True when Settings:Incremental is on.</returns>
    /// <remarks>
    /// The same setting the engine reads to decide the mode it asks the store
    /// for, on purpose. The store may still escalate the run to full - no
    /// checkpoint yet, the hash framing changed, Settings:FullEveryHours elapsed
    /// - and when it does, this connector keeps reading the marker-bearing view
    /// with no lower bound. That is not a disagreement: it is how the first
    /// checkpoint is created, and it is why turning the setting on works from a
    /// standing start rather than needing a manual first step.
    /// </remarks>
    public static bool ReadsIncrementally(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Setting("Incremental", false);
    }

    /// <inheritdoc/>
    public PushItem? MapRow(SqlDataReader reader, PushOptions options)
    {
        var item = new PushItem
        {
            Id = SqlRead.Text(reader, "ItemId"),
            ItemType = SqlRead.Text(reader, "ItemType"),
            Content = SqlRead.Text(reader, "Content"),
        };

        // Null properties are omitted rather than sent as null: a customer has no
        // consultant, and Graph rejects a null value rather than ignoring it.
        item.Properties["itemType"] = item.ItemType;
        item.Properties["title"] = SqlRead.Text(reader, "Title");
        item.Properties["url"] = SqlRead.Text(reader, "Url");
        item.Properties["lastModified"] = SqlRead.Utc(reader, "LastModified");
        item.Properties["hierarchyPath"] = SqlRead.Text(reader, "HierarchyPath");
        item.Properties["customerName"] = SqlRead.Text(reader, "CustomerName");
        item.Properties["customerCode"] = SqlRead.Text(reader, "CustomerCode");
        item.Properties["accountManager"] = SqlRead.Text(reader, "AccountManager");
        item.Properties["industry"] = SqlRead.Text(reader, "Industry");
        item.Properties["region"] = SqlRead.Text(reader, "Region");

        item.AddIfPresent("containerName", SqlRead.Text(reader, "ContainerName"));
        item.AddIfPresent("containerUrl", SqlRead.Text(reader, "ContainerUrl"));
        item.AddIfPresent("engagementName", SqlRead.Text(reader, "EngagementName"));
        item.AddIfPresent("engagementCode", SqlRead.Text(reader, "EngagementCode"));
        item.AddIfPresent("projectManager", SqlRead.Text(reader, "ProjectManager"));
        item.AddIfPresent("practice", SqlRead.Text(reader, "Practice"));
        item.AddIfPresent("status", SqlRead.Text(reader, "Status"));
        item.AddIfPresent("consultantName", SqlRead.Text(reader, "ConsultantName"));
        item.AddIfPresent("consultantEmail", SqlRead.Text(reader, "ConsultantEmail"));
        item.AddIfPresent("workType", SqlRead.Text(reader, "WorkType"));
        item.AddIfPresent("workDate", SqlRead.NullableUtc(reader, "WorkDate"));
        item.AddIfPresent("hours", SqlRead.Number(reader, "Hours"));
        item.AddIfPresent("billable", SqlRead.Flag(reader, "Billable"));
        item.AddIfPresent("contractValue", SqlRead.Number(reader, "ContractValue"));
        item.AddIfPresent("totalHours", SqlRead.Number(reader, "TotalHours"));

        double? childCount = SqlRead.Number(reader, "ChildCount");

        if (childCount.HasValue)
        {
            if (childCount.Value != Math.Floor(childCount.Value))
            {
                // The shipped view CASTs this to INT; a repointed view might not,
                // and a silent truncation would put a wrong count in the index.
                throw new InvalidOperationException(
                    $"ChildCount for item {item.Id} is not a whole number. The view must produce an integer.");
            }

            item.AddIfPresent("childCount", (long?)childCount.Value);
        }

        return item;
    }

    /// <summary>
    /// Adds the connector's own configuration rules to the family's.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator, so every problem is reported at once.</param>
    /// <remarks>
    /// One rule, and it exists because of the exact mistake this feature was
    /// blocked on for the whole of its life. Settings:Incremental with
    /// Source:ItemView still pointing at dbo.vwExternalItems selects a column
    /// that view does not have, and SQL Server answers with "Invalid column
    /// name 'EffectiveLastModified'" at the first read - exit 4, after the
    /// connection and schema have been registered, naming a column rather than a
    /// setting. Caught here it is exit 2 with a configuration key and the name
    /// of the view to use, before anything is opened.
    ///
    /// Only the connector's OWN default view is refused, not every view without
    /// the column. A deployment reading a filtered or repointed marker view is a
    /// legitimate thing to do and this cannot know the shape of it; that case
    /// still fails at the first read, loudly, which is the right outcome for a
    /// view nobody here has seen.
    /// </remarks>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        if (ReadsIncrementally(options) &&
            string.Equals(options.Source.ItemView, this.DefaultItemView, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Source:ItemView",
                $"is {this.DefaultItemView}, which carries no EffectiveLastModified column, but " +
                $"Settings:Incremental is on. Point it at {IncrementalItemView} (sql/26), or turn " +
                "Settings:Incremental off to read the whole source every run.");
        }
    }

    /// <summary>
    /// Fills in the view this connector reads when configuration does not name
    /// one, choosing by whether the run reads incrementally.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <remarks>
    /// Explicit, and it REPLACES the one ISqlPushConnector supplies rather than
    /// adding to it - which is why the "if it is blank" test is repeated here
    /// rather than inherited. The family's version knows only one default view;
    /// this connector has two, and which is right depends on a setting the
    /// family has no reason to read.
    /// </remarks>
    void IPushConnector.ApplyDefaults(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            options.Source.ItemView = ReadsIncrementally(options)
                ? IncrementalItemView
                : this.DefaultItemView;
        }
    }

    /// <summary>Opens the source, of whichever of the two kinds this run needs.</summary>
    /// <param name="context">Configuration, credential, logger and the resume marker.</param>
    /// <returns>The source, disposed by the host when the run ends.</returns>
    /// <remarks>
    /// The two differ in more than a query. SqlPushSource declares
    /// SourceChangeDetection.Differencing and RequiresOrderedCommit = false, so
    /// the engine may open the run only as full and may write with sixteen
    /// concurrent writers. HierarchyIncrementalSource declares ChangeMarker and
    /// RequiresOrderedCommit = true, so the engine may read a slice and must
    /// write serially. Neither pair of answers is safe for the other's query,
    /// which is why this is a choice of class and not a flag.
    /// </remarks>
    IPushSource IPushConnector.CreateSource(PushSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ReadsIncrementally(context.Options)
            ? new HierarchyIncrementalSource(this, context)
            : new SqlPushSource(this, context);
    }
}
