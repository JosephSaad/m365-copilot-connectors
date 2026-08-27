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

        List<AtlasEntity> ordered = candidates
            .OrderBy(entity => entity.UpdatedUtc)
            .ThenBy(entity => entity.Guid, StringComparer.Ordinal)
            .Where(entity => this.fullRecrawl || this.checkpoint.IsAfter(entity.UpdatedUtc, entity.Guid))
            .ToList();

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
                    "written this run; the next run continues from the marker this one reached.",
                    this.settings.MaxItemsPerRun,
                    ordered.Count - yielded);
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

        // A database entry is described to whoever may read anything in it; a
        // table entry to whoever may read that table. A path entry follows the
        // filesystem rules the HDFS source already applies.
        RoutingDecision decision = entity.Kind switch
        {
            AtlasEntityKind.Table => this.routing!.EvaluateCatalogueEntry(database, table),
            AtlasEntityKind.Database => this.routing!.EvaluateCatalogueEntry(database, "*"),
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
        if (this.settings.AtlasIncludeLineage)
        {
            await this.atlas.AddLineageAsync(entity, cancellationToken);
        }

        IReadOnlyList<string> describable = entity.Kind == AtlasEntityKind.Table
            ? this.routing!.CatalogueColumns(database, table)
            : Array.Empty<string>();

        List<string> columns = describable.Count == 0
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
            Acl = grants,
            Content = Describe(entity, kind, columns),
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
        item.AddIfPresent("classifications", string.Join(", ", entity.Classifications));
        item.AddIfPresent("glossaryTerms", string.Join(", ", entity.Terms));
        item.AddIfPresent("upstream", string.Join(", ", entity.Upstream));
        item.AddIfPresent("downstream", string.Join(", ", entity.Downstream));
        item.AddIfPresent("columnCount", (long)columns.Count);
        item.AddIfPresent("modifiedUtc", entity.UpdatedUtc.UtcDateTime.ToString("o"));

        this.markers[item.Id] = (entity.UpdatedUtc.UtcDateTime.ToString("o"), entity.Guid);

        return item;
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
