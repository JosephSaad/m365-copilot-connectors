// ---------------------------------------------------------------------------
// AtlasPushSource.cs
// The data catalogue, made searchable.
//
// This source answers a question the other two cannot: "where does this field
// come from, who owns this table, and what feeds it". Those are asked in words,
// about named things, by people who do not know where to look - which is
// exactly what an index is for.
//
// WHO MAY SEE A CATALOGUE ENTRY. This is the decision worth reviewing, because
// the connector is deliberately STRICTER than the cluster.
//
// Atlas has its own authorization, a separate Ranger service from Hadoop SQL,
// and CDP ships it with a policy called "public" that grants every
// authenticated user read on every entity. So on a default cluster, the
// catalogue is readable by everyone with an account. This connector does not
// mirror that. "Everyone with a cluster account" and "everyone in the Microsoft
// 365 tenant" are different populations, and an index that made the second
// inherit the first would publish the shape of the lake - table names, column
// names, owners - to people who cannot reach the cluster at all.
//
// Instead an entry is granted to exactly the groups Ranger grants SELECT on the
// table it describes, and skipped when that is nobody. Narrower than the
// source is the safe direction to be wrong in.
//
// A ROW FILTER OR A COLUMN MASK DOES NOT REFUSE AN ENTRY, and that is the other
// deliberate departure. A filter governs which rows a person sees and a mask
// which values; neither hides the table's existence, its columns or its owner
// from somebody granted select - they see all of it the moment they query. So
// the metadata of a filtered table is indexable for those people even though
// its data is not, and those are frequently the tables a catalogue is most
// needed for, because their contents can never be indexed.
//
// A column-scoped grant narrows rather than refuses: only the columns named are
// described. A column name discloses - one called "hiv_status" says something
// by existing - and somebody granted three columns has not been shown forty.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Atlas;

using System.Runtime.CompilerServices;
using System.Text;
using CdpConnector.Source.Acl;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using PushCore;
using Serilog;

/// <summary>Reads the Atlas catalogue and yields one item per described entity.</summary>
public sealed class AtlasPushSource : IPushSource
{
    private readonly CdpSettings settings;
    private readonly AtlasClient atlas;
    private readonly RangerPolicyClient ranger;
    private readonly PrincipalResolver principals;
    private readonly CheckpointStore checkpoints;
    private readonly CrawlCheckpoint checkpoint;
    private readonly ILogger log;
    private readonly bool ownsClients;

    private readonly Dictionary<string, (string Time, string Key)> markers = new(StringComparer.Ordinal);

    private readonly bool fullRecrawl;

    private RoutingEvaluator? routing;
    private bool truncated;
    private int skipped;
    private string pendingMarkerTime = string.Empty;
    private string pendingMarkerKey = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="AtlasPushSource"/> class.</summary>
    /// <param name="settings">Validated CDP settings.</param>
    /// <param name="atlas">Reads the catalogue.</param>
    /// <param name="ranger">Reads the policies that decide who may see an entry.</param>
    /// <param name="principals">Turns cluster group names into Entra grants.</param>
    /// <param name="checkpoints">Where the watermark is kept.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="ownsClients">True when disposing this should dispose the clients.</param>
    public AtlasPushSource(
        CdpSettings settings,
        AtlasClient atlas,
        RangerPolicyClient ranger,
        PrincipalResolver principals,
        CheckpointStore checkpoints,
        ILogger log,
        bool ownsClients = true)
    {
        this.settings = settings;
        this.atlas = atlas;
        this.ranger = ranger;
        this.principals = principals;
        this.checkpoints = checkpoints;
        this.checkpoint = checkpoints.Read();
        this.log = log;
        this.ownsClients = ownsClients;

        this.fullRecrawl = settings.FullRecrawlEveryRuns > 0 &&
            (this.checkpoint.RunCount % settings.FullRecrawlEveryRuns) == 0;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <summary>
    /// A deterministic item ID for an Atlas entity.
    ///
    /// An Atlas GUID is a UUID, so stripping its hyphens leaves 32 characters
    /// that are already ASCII alphanumeric and well inside the 128 Graph
    /// allows. Hashing it would only make it unreadable in a log.
    /// </summary>
    /// <param name="guid">The Atlas GUID.</param>
    /// <returns>The item ID.</returns>
    public static string ItemId(string guid)
    {
        var builder = new StringBuilder("a", 40);

        foreach (char character in guid)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.Length > 128 ? builder.ToString(0, 128) : builder.ToString();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Before the catalogue is read, for the same reason the other sources
        // read it first: the policies are what say who may see any of this.
        this.routing = new RoutingEvaluator(
            await this.ranger.PoliciesAsync(this.settings.RangerSqlService, cancellationToken));

        var candidates = new List<AtlasEntity>();

        foreach (string typeName in this.settings.AtlasTypes)
        {
            candidates.AddRange(await this.atlas.SearchAsync(typeName, this.settings.AtlasPageSize, cancellationToken));
        }

        // The detail fetch has to happen BEFORE the marker is applied, and that
        // ordering is not incidental. A basic-search hit carries no updateTime
        // - only GET /entity/guid/{guid} does - so filtering on what the search
        // returned would compare every entity at the epoch, and once a marker
        // existed the filter would reject all of them and the catalogue would
        // stop updating while reporting success.
        //
        // Atlas 2.1.0 also cannot filter a basic search by modification time,
        // so the whole catalogue is enumerated and detailed every run whatever
        // the marker says. What the marker spares is the GRAPH WRITES, which
        // cost item quota; the Atlas reads cost seconds against a catalogue of
        // thousands rather than millions.
        foreach (AtlasEntity entity in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.atlas.EnrichAsync(entity, cancellationToken);
        }

        // The slack is the same one the HDFS source applies, and it is needed
        // here for a reason particular to this source. Each entity's timestamp
        // is read by its own detail call, so the timestamps in one run are
        // snapshots taken minutes apart across the enrich loop rather than at
        // one instant. An entity altered after its own snapshot but before a
        // later entity pushed the marker past it would be filtered out on every
        // later incremental run, and would sit stale - content AND ACL - until
        // the next full recrawl. Re-reading a few entities is upsert-cheap; not
        // reading a changed one for a week is not.
        List<AtlasEntity> ordered = candidates
            .OrderBy(entity => entity.UpdatedUtc)
            .ThenBy(entity => entity.Guid, StringComparer.Ordinal)
            .Where(entity => this.fullRecrawl ||
                             this.checkpoint.IsAfter(
                                 entity.UpdatedUtc, entity.Guid, this.settings.ScanSlackSeconds))
            .ToList();

        if (this.settings.ItemBudget > 0 && ordered.Count > this.settings.ItemBudget)
        {
            throw new InvalidOperationException(
                $"{ordered.Count} catalogue entr(y/ies) are in scope, above the configured " +
                $"Settings:ItemBudget of {this.settings.ItemBudget}. Nothing has been written. Raise the " +
                "budget deliberately, or narrow Settings:AtlasTypes - a budget exists so a catalogue that " +
                "grew by an order of magnitude overnight stops rather than spends the tenant's item quota.");
        }

        this.log.Information(
            "{Total} catalogue entit(y/ies) found, {Selected} to write this run{Mode}.",
            candidates.Count,
            ordered.Count,
            this.fullRecrawl ? " (full recrawl)" : " (incremental)");

        int yielded = 0;

        foreach (AtlasEntity entity in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.settings.MaxItemsPerRun > 0 && yielded >= this.settings.MaxItemsPerRun)
            {
                this.truncated = true;

                this.log.Warning(
                    "Stopping at Settings:MaxItemsPerRun ({Cap}). {Remaining} catalogue entr(y/ies) were not " +
                    "written this run; {Next}",
                    this.settings.MaxItemsPerRun,
                    ordered.Count - yielded,
                    this.fullRecrawl
                        ? "this run is a full recrawl, which starts from the beginning every time, so it " +
                          "cannot complete while the cap is below the size of the catalogue. Raise " +
                          "Settings:MaxItemsPerRun above it or the recrawl will keep re-writing the same " +
                          "oldest entries."
                        : "the next run continues from the marker this one reached.");
                break;
            }

            PushItem? item = await this.MapAsync(entity, cancellationToken);

            if (item is null)
            {
                continue;
            }

            yielded++;
            yield return item;
        }
    }

    /// <inheritdoc/>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        if (this.markers.TryGetValue(item.Id, out (string Time, string Key) marker))
        {
            this.pendingMarkerTime = marker.Time;
            this.pendingMarkerKey = marker.Key;
            this.markers.Remove(item.Id);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        CrawlCheckpoint stored = this.checkpoints.Read();

        // Monotonic, for the reason the HDFS source is: a marker that can move
        // backwards has no invariant at all.
        if (this.pendingMarkerTime.Length > 0 &&
            stored.IsAfter(ParseMarker(this.pendingMarkerTime), this.pendingMarkerKey))
        {
            stored.MarkerTime = this.pendingMarkerTime;
            stored.MarkerKey = this.pendingMarkerKey;
        }

        // A run the cap cut short did not cover the catalogue, and the cadence
        // counts crawls that did - re-deriving every entry's ACL is what it
        // exists for.
        if (!this.truncated)
        {
            stored.RunCount = this.checkpoint.RunCount + 1;
        }

        stored.LastCompletedUtc = DateTimeOffset.UtcNow.ToString("o");
        this.checkpoints.Write(stored);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (this.ownsClients)
        {
            this.atlas.Dispose();
            this.ranger.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset ParseMarker(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }

    /// <summary>Turns one entity into a catalogue item, or null when it must not be described.</summary>
    private async Task<PushItem?> MapAsync(AtlasEntity entity, CancellationToken cancellationToken)
    {
        if (entity.Kind == AtlasEntityKind.Other)
        {
            this.skipped++;
            return null;
        }

        (string database, string table, string cluster) =
            AtlasEntity.SplitQualifiedName(entity.QualifiedName);

        // A table whose qualified name did not parse into a database and a table
        // cannot be asked about. An empty table name matches a policy written
        // over "*" and nothing else, so the entry would be granted by the
        // database-wide policy while a deny naming the real table could never
        // match it. Refusing is the only answer that is not a guess.
        if (entity.Kind == AtlasEntityKind.Table && (database.Length == 0 || table.Length == 0))
        {
            this.skipped++;

            this.log.Warning(
                "The catalogue entry for {QualifiedName} has no database.table form, so Ranger cannot be " +
                "asked who may see it, and it is not indexed.",
                entity.QualifiedName);

            return null;
        }

        // A database entry is described to whoever may read anything in it; a
        // table entry to whoever may read that table. A path entry follows the
        // filesystem rules the HDFS source already applies.
        RoutingDecision decision = entity.Kind switch
        {
            AtlasEntityKind.Table => this.routing!.EvaluateCatalogueEntry(database, table),
            AtlasEntityKind.Database => this.routing!.EvaluateDatabaseEntry(database),
            _ => this.routing!.EvaluatePath(entity.QualifiedName),
        };

        if (!decision.MayIndex || decision.Groups.Count == 0)
        {
            this.skipped++;
            return null;
        }

        IReadOnlyList<PushAclEntry> grants =
            await this.principals.ResolveAsync(decision.Groups, cancellationToken);

        if (grants.Count == 0)
        {
            this.skipped++;
            this.log.Warning(
                "The catalogue entry for {QualifiedName} resolves to no Entra group and is not indexed.",
                entity.QualifiedName);
            return null;
        }

        // Enriched already, in the gather phase, because the marker needed the
        // timestamp it carries. Lineage is the expensive optional half and is
        // fetched only for entries that survived the routing check.
        //
        // Not for a database. Atlas serves lineage for entities deriving from
        // DataSet or Process, and a hive_db derives from neither, so asking is a
        // 400 from a completely healthy cluster - and one request per database
        // that can only fail is a request not worth making.
        if (this.settings.AtlasIncludeLineage && entity.Kind != AtlasEntityKind.Database)
        {
            await this.AddVisibleLineageAsync(entity, decision.Groups, cancellationToken);
        }

        IReadOnlyList<string>? describable = entity.Kind == AtlasEntityKind.Table
            ? this.routing!.CatalogueColumns(database, table)
            : null;

        // Null is "nothing narrows this"; an empty list is "the granting
        // policies agree on no column", and those are opposite answers.
        List<string> columns = describable is null
            ? entity.Columns.ToList()
            : entity.Columns
                .Where(column => describable.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToList();

        string kind = entity.Kind switch
        {
            AtlasEntityKind.Database => "database",
            AtlasEntityKind.Table => "table",
            _ => "path",
        };

        var item = new PushItem
        {
            Id = ItemId(entity.Guid),
            ItemType = "catalogue",
            // NOT SET, and the derivation above is still run on purpose.
            // Control ACL-1: every item is granted the connector's single AD
            // group, so leaving Acl null is what makes the engine apply it.
            // What the Ranger and HDFS derivation still buys is the gate above -
            // an object the cluster grants to nobody is skipped rather than
            // handed the group - which is the verification half of the rule.
            // Acl = grants,
            Content = Describe(entity, kind, columns),

            // Handed to the engine RAW, in Atlas's own vocabulary, and not
            // interpreted here. What a tag means - whether it is a label, and
            // whether an entity carrying it may be indexed at all - is a policy
            // decision that has to hold for every source, so it lives in
            // SensitivityPolicy and is configured once. This source's job ends
            // at saying what Atlas said.
            //
            // Note what is NOT in this list, because it bounds what any policy
            // built on it can claim: column-level tags. AtlasClient reads a
            // table's columns as display names only, and CdpSettings refuses
            // hive_column outright, so a PII tag applied to one column of a
            // table is invisible here. Atlas deployments commonly tag exactly
            // that way.
            Classifications = entity.Classifications.ToList(),
        };

        item.AddIfPresent("title", entity.Name.Length > 0 ? entity.Name : entity.QualifiedName);
        item.AddIfPresent("entityType", entity.TypeName);
        item.AddIfPresent("entityKind", kind);
        item.AddIfPresent("qualifiedName", entity.QualifiedName);
        item.AddIfPresent("databaseName", database);
        item.AddIfPresent("clusterName", cluster);
        item.AddIfPresent("ownerName", entity.Owner);
        item.AddIfPresent("description", entity.Description);
        item.AddIfPresent("columnNames", string.Join(", ", columns));

        // Collections, not joined strings, because both are refiners. A refiner
        // buckets on the whole stored value, so "PII, GDPR" would be a bucket of
        // its own and filtering on PII would miss the table carrying both tags -
        // which is the one query these two fields exist to answer.
        item.AddIfPresent("classifications", entity.Classifications.ToList());
        item.AddIfPresent("glossaryTerms", entity.Terms.ToList());

        item.AddIfPresent("upstream", string.Join(", ", entity.Upstream));
        item.AddIfPresent("downstream", string.Join(", ", entity.Downstream));
        item.AddIfPresent("columnCount", (long)columns.Count);

        // An entity whose detail call 404'd carries no timestamp at all, and
        // writing the default would put 0001-01-01 in the field Copilot shows as
        // the last modified date. No date is better than a wrong one.
        if (entity.UpdatedUtc != default)
        {
            item.AddIfPresent("modifiedUtc", entity.UpdatedUtc.UtcDateTime.ToString("o"));
        }

        this.markers[item.Id] = (entity.UpdatedUtc.UtcDateTime.ToString("o"), entity.Guid);

        return item;
    }

    /// <summary>
    /// Fills in the lineage a reader of this entry is allowed to be told about.
    ///
    /// A neighbour's NAME is a disclosure. "Produced from hr.salaries_raw" tells
    /// everyone granted the downstream table that a table of salaries exists,
    /// what it is called and which database holds it - and that entry's ACL is
    /// the downstream table's, which has nothing to do with who may read the
    /// upstream one. Atlas will not stop this: on a stock cluster its own policy
    /// shows every authenticated user every entity, which is exactly why this
    /// connector does its own check.
    ///
    /// The test is that every group on THIS entry is also granted the
    /// neighbour. Not "somebody is granted it" - the item carries one ACL and
    /// every group on it sees every word - and not "the sets overlap", which
    /// would disclose to the groups in the difference. A neighbour that is not a
    /// Hive table, or whose qualified name will not parse, is dropped rather
    /// than guessed at.
    /// </summary>
    /// <param name="entity">The entity being described.</param>
    /// <param name="granted">The groups this entry will be granted to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    private async Task AddVisibleLineageAsync(
        AtlasEntity entity, IReadOnlyList<string> granted, CancellationToken cancellationToken)
    {
        (IReadOnlyList<AtlasNeighbour> upstream, IReadOnlyList<AtlasNeighbour> downstream) =
            await this.atlas.LineageAsync(entity.Guid, cancellationToken);

        int hidden = 0;

        foreach ((IReadOnlyList<AtlasNeighbour> neighbours, IList<string> into) in
                 new[] { (upstream, entity.Upstream), (downstream, entity.Downstream) })
        {
            foreach (AtlasNeighbour neighbour in neighbours)
            {
                if (this.MayName(neighbour, granted))
                {
                    into.Add(neighbour.Name);
                }
                else
                {
                    hidden++;
                }
            }
        }

        if (hidden > 0)
        {
            this.log.Debug(
                "{Hidden} lineage neighbour(s) of {QualifiedName} are not named in its catalogue entry, " +
                "because not everybody granted the entry is granted them.",
                hidden,
                entity.QualifiedName);
        }
    }

    /// <summary>Decides whether everybody granted this entry may also be told the neighbour exists.</summary>
    /// <param name="neighbour">The dataset on the other end of the lineage hop.</param>
    /// <param name="granted">The groups the entry will be granted to.</param>
    /// <returns>True when every one of those groups is granted the neighbour too.</returns>
    private bool MayName(AtlasNeighbour neighbour, IReadOnlyList<string> granted)
    {
        (string database, string table, _) = AtlasEntity.SplitQualifiedName(neighbour.QualifiedName);

        if (database.Length == 0 || table.Length == 0)
        {
            return false;
        }

        RoutingDecision decision = this.routing!.EvaluateCatalogueEntry(database, table);

        if (!decision.MayIndex)
        {
            return false;
        }

        return granted.All(group => decision.Groups.Contains(group, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The body somebody would actually search for.
    ///
    /// Written as sentences rather than a field dump, because the query this
    /// has to answer is "which table has the customer's address in it" and the
    /// words around a value are what make it match.
    /// </summary>
    private static string Describe(AtlasEntity entity, string kind, IReadOnlyList<string> columns)
    {
        var text = new StringBuilder();

        text.Append(char.ToUpperInvariant(kind[0])).Append(kind.AsSpan(1)).Append(' ')
            .Append(entity.QualifiedName).Append('.').AppendLine();

        if (entity.Owner.Length > 0)
        {
            text.Append("Owned by ").Append(entity.Owner).Append('.').AppendLine();
        }

        if (entity.Description.Length > 0)
        {
            text.AppendLine(entity.Description);
        }

        if (entity.Comment.Length > 0 &&
            !string.Equals(entity.Comment, entity.Description, StringComparison.Ordinal))
        {
            text.AppendLine(entity.Comment);
        }

        if (columns.Count > 0)
        {
            text.Append("Columns: ").Append(string.Join(", ", columns)).Append('.').AppendLine();
        }

        if (entity.Classifications.Count > 0)
        {
            text.Append("Classified as ").Append(string.Join(", ", entity.Classifications)).Append('.').AppendLine();
        }

        if (entity.Terms.Count > 0)
        {
            text.Append("Glossary terms: ").Append(string.Join(", ", entity.Terms)).Append('.').AppendLine();
        }

        if (entity.Upstream.Count > 0)
        {
            text.Append("Produced from ").Append(string.Join(", ", entity.Upstream)).Append('.').AppendLine();
        }

        if (entity.Downstream.Count > 0)
        {
            text.Append("Feeds ").Append(string.Join(", ", entity.Downstream)).Append('.').AppendLine();
        }

        return text.ToString().Trim();
    }
}
