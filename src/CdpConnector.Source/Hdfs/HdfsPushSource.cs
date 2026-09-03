// ---------------------------------------------------------------------------
// HdfsPushSource.cs
// The HDFS crawl: which files, in what order, with whose permissions, and what
// the connector is allowed to remember about how far it got.
//
// Ordering first, because everything else depends on it. Files are gathered
// from the configured roots and sorted by (modification time, path) before any
// of them is read. That is the same composite ordering the SQL family uses, and
// it is what makes the checkpoint exact: a run interrupted at any point has
// written a prefix of that order, so the marker identifies precisely what is
// left. Sorting means holding the listing in memory - one status record per
// file, not one file - which for a lake of a few million files is tens of
// megabytes and worth it.
//
// The periodic full recrawl is a security control, not a completeness one. A
// permission change at the source does not alter a file's modification time, so
// an incremental pass never revisits a file whose group grant was revoked, and
// its indexed ACL would stay stale indefinitely. Settings:FullRecrawlEveryRuns
// is therefore the documented upper bound on that staleness, and the same
// mechanism happens to catch the other thing an mtime watermark cannot see: a
// file moved into scope carrying an older timestamp.
//
// The error budget exists so that a systemically broken extractor or a sick
// DataNode cannot be laundered into a successful crawl of skips. Past the
// threshold the run fails, which leaves the watermark where it was.
//
// Two rules keep the watermark meaningful, and they are one defect apart. The
// marker only ever moves FORWARDS, and a run that did not reach the end of what
// it set out to read does not count as a crawl. A full recrawl ignores the
// marker and so reads the corpus oldest-first; a run of it truncated by
// Settings:MaxItemsPerRun therefore ends on an EARLY file, and writing that
// position over a later high-water mark - while counting the run as a completed
// crawl - abandons everything between the two, permanently, because the next
// run starts from the earlier position again. So FlushMarker refuses a marker
// that is not strictly after the stored one, and a truncated run leaves the run
// counter alone so the next run re-enters the recrawl rather than abandoning it.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hdfs;

using System.Runtime.CompilerServices;
using Connector.Extraction;
using CdpConnector.Source.Acl;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using PushCore;
using Serilog;

/// <summary>Crawls HDFS and yields one item per indexable file.</summary>
public sealed class HdfsPushSource : IPushSource
{
    private readonly CdpSettings settings;
    private readonly WebHdfsClient hdfs;
    private readonly RangerPolicyClient ranger;
    private readonly HdfsAclBuilder acls;
    private readonly TextExtractorSet extractors;
    private readonly CheckpointStore checkpoints;
    private readonly CrawlCheckpoint checkpoint;
    private readonly bool fullRecrawl;
    private readonly ILogger log;
    private readonly bool ownsHdfs;

    /// <summary>
    /// What each file in scope inherited from the directories above it: the
    /// groups that can traverse all of them, or null when none restricted
    /// anybody, and whether the whole chain is world-traversable.
    ///
    /// Recorded during the walk because that is the only place the chain is
    /// known. A file missing from here has not been walked to, and is gated
    /// closed rather than assumed reachable.
    /// </summary>
    private readonly Dictionary<string, (IReadOnlySet<string>? Gate, bool Everyone)> reachable =
        new(StringComparer.Ordinal);

    private RoutingEvaluator? routing;
    private bool truncated;
    private int skipped;
    private int examined;
    private int failures;
    private int commitsSinceFlush;
    private string pendingMarkerTime = string.Empty;
    private string pendingMarkerKey = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="HdfsPushSource"/> class.</summary>
    /// <param name="settings">Validated CDP settings.</param>
    /// <param name="hdfs">The WebHDFS or HttpFS client.</param>
    /// <param name="ranger">Reads the Ranger policies that decide what may be indexed.</param>
    /// <param name="acls">Builds each file's grants.</param>
    /// <param name="extractors">Turns a file into text.</param>
    /// <param name="checkpoints">Where the watermark is kept.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="ownsHdfs">True when disposing this should dispose the client.</param>
    public HdfsPushSource(
        CdpSettings settings,
        WebHdfsClient hdfs,
        RangerPolicyClient ranger,
        HdfsAclBuilder acls,
        TextExtractorSet extractors,
        CheckpointStore checkpoints,
        ILogger log,
        bool ownsHdfs = true)
    {
        this.settings = settings;
        this.hdfs = hdfs;
        this.ranger = ranger;
        this.acls = acls;
        this.extractors = extractors;
        this.checkpoints = checkpoints;
        this.log = log;
        this.ownsHdfs = ownsHdfs;

        this.checkpoint = checkpoints.Read();

        this.fullRecrawl = settings.FullRecrawlEveryRuns > 0 &&
            (this.checkpoint.RunCount % settings.FullRecrawlEveryRuns) == 0;

        if (this.fullRecrawl && this.checkpoint.HasMarker)
        {
            this.log.Information(
                "Run {RunCount} is a full recrawl (every {Every} runs). Every file is re-read, which is what " +
                "re-derives item ACLs after a permission change at the source and picks up files moved into " +
                "scope with older timestamps.",
                this.checkpoint.RunCount + 1,
                settings.FullRecrawlEveryRuns);
        }
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Before the first listing, deliberately. If the policies cannot be read
        // this throws, and nothing has been crawled - which is the only safe
        // order when the policies are what say whether crawling is allowed.
        this.routing = new RoutingEvaluator(
            await this.ranger.PoliciesAsync(this.settings.RangerHdfsService, cancellationToken));

        IReadOnlyList<HdfsFileStatus> candidates = await this.GatherAsync(cancellationToken);

        int yielded = 0;

        foreach (HdfsFileStatus file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.settings.MaxItemsPerRun > 0 && yielded >= this.settings.MaxItemsPerRun)
            {
                // The break ends the iterator normally, so the engine will call
                // OnCrawlCompletedAsync as if the crawl had finished. This flag
                // is what stops that being recorded as a crawl of the corpus.
                this.truncated = true;

                this.log.Warning(
                    "Stopping at Settings:MaxItemsPerRun ({Cap}). {Remaining} file(s) in scope were not read " +
                    "this run; the watermark advances only over what was written and never backwards, and " +
                    "this run does not count towards Settings:FullRecrawlEveryRuns, so the next run continues " +
                    "{Mode}.",
                    this.settings.MaxItemsPerRun,
                    candidates.Count - yielded,
                    this.fullRecrawl
                        ? "by re-entering the full recrawl, which cannot complete at all while the cap is " +
                          "below the number of files in scope"
                        : "from the marker");
                break;
            }

            PushItem? item = await this.MapAsync(file, cancellationToken);

            if (item is null)
            {
                continue;
            }

            this.CheckErrorBudget();

            yielded++;
            yield return item;
        }
    }

    /// <inheritdoc/>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        // The engine has written this item, so the marker may move to it - and
        // only to it. Everything after this point in the ordering is still
        // unwritten as far as any resume is concerned.
        this.pendingMarkerTime = (string)item.Properties["modifiedUtc"];
        this.pendingMarkerKey = (string)item.Properties["itemPath"];
        this.commitsSinceFlush++;

        // Flushed periodically rather than per item: a crash loses at most this
        // many items' worth of progress, and re-reading them is an upsert.
        if (this.commitsSinceFlush >= 200)
        {
            this.FlushMarker();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        // Reached when the enumeration ended and every write returned, which is
        // the only place the run counter may advance. A failed run leaves the
        // counter alone, and so does a run the per-run cap cut short: the
        // full-recrawl cadence counts crawls that covered the corpus, not
        // attempts and not partial passes.
        this.FlushMarker();

        CrawlCheckpoint stored = this.checkpoints.Read();

        if (!this.truncated)
        {
            // The cadence counts crawls that actually COVERED the corpus, not
            // runs that returned without throwing. Re-deriving item ACLs after a
            // permission change at the source is the entire reason the cadence
            // exists, and a recrawl that stopped at Settings:MaxItemsPerRun did
            // not re-derive the ACLs of the files it never reached. Counting it
            // would abandon the recrawl for another FullRecrawlEveryRuns runs on
            // the strength of work that was not done.
            stored.RunCount = this.checkpoint.RunCount + 1;
        }

        stored.LastCompletedUtc = DateTimeOffset.UtcNow.ToString("o");
        this.checkpoints.Write(stored);

        this.log.Information(
            "Crawl {Outcome}. {Examined} file(s) examined, {Failures} failed extraction or read, " +
            "watermark at {Marker}.",
            this.truncated
                ? "stopped early at the per-run cap and does not count as a crawl of the corpus"
                : "complete",
            this.examined,
            this.failures,
            stored.MarkerTime.Length == 0 ? "(none)" : stored.MarkerTime);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (this.ownsHdfs)
        {
            this.hdfs.Dispose();
            this.ranger.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Walks the configured roots and returns the files to consider, in the
    /// order the checkpoint understands.
    /// </summary>
    private async Task<IReadOnlyList<HdfsFileStatus>> GatherAsync(CancellationToken cancellationToken)
    {
        var found = new List<HdfsFileStatus>();

        // Ordinal, because HDFS paths are case-sensitive. /data/root/HR and
        // /data/root/hr are two different directories on the cluster, and
        // comparing them case-insensitively drops the second one and its whole
        // subtree with no warning and no failure. The roots are normalised once
        // in CdpSettings, so a root written with a trailing slash is the same
        // string as the one this walk derives from a listing.
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (string root in this.settings.HdfsRoots)
        {
            RoutingDecision decision = this.routing!.EvaluatePath(root);

            if (!decision.MayIndex)
            {
                this.log.Warning(
                    "Root {Root} is not indexed: {Reason} (Ranger polic(y/ies) {PolicyIds})",
                    root,
                    decision.Reason,
                    string.Join(", ", decision.PolicyIds));
                continue;
            }

            // The root's own permissions gate everything under it, so it is
            // narrowed like any other directory before the walk begins. A root
            // whose status cannot be read leaves the gate unrestricted rather
            // than empty, because an unreadable status is not evidence that
            // nobody may traverse - and the files inside it are still gated by
            // every directory below.
            HdfsFileStatus? rootStatus = await this.hdfs.StatusAsync(root, cancellationToken);

            (IReadOnlySet<string>? gate, bool everyone) = rootStatus is null
                ? (null, true)
                : await this.NarrowAsync(rootStatus, gate: null, everyone: true, cancellationToken);

            await this.WalkAsync(root, found, visited, gate, everyone, cancellationToken);
        }

        int before = found.Count;

        List<HdfsFileStatus> ordered = found
            .Where(file => this.fullRecrawl ||
                           this.checkpoint.IsAfter(file.ModifiedUtc, file.Path, this.settings.ScanSlackSeconds))
            .OrderBy(file => file.ModifiedUtc)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        this.log.Information(
            "{Total} file(s) in scope, {Selected} to read this run{Mode}.",
            before,
            ordered.Count,
            this.fullRecrawl ? " (full recrawl)" : " (incremental)");

        if (this.settings.ItemBudget > 0 && ordered.Count > this.settings.ItemBudget)
        {
            // Before a single write, so this is exit 4 with a number in it rather
            // than a connection discovering its own ceiling halfway through.
            throw new InvalidOperationException(
                $"{ordered.Count} item(s) are in scope, above the configured Settings:ItemBudget of " +
                $"{this.settings.ItemBudget}. Raise the budget deliberately, or narrow Settings:HdfsRoots and " +
                "Settings:IncludeExtensions. Nothing was written.");
        }

        return ordered;
    }

    /// <summary>
    /// Narrows the traversal gate by one directory.
    ///
    /// A world-traversable directory restricts nobody, so it passes the
    /// inherited gate through untouched. Any other directory intersects it with
    /// the groups that may traverse THIS directory, and the result is what the
    /// files below it inherit.
    /// </summary>
    /// <param name="directory">The directory being entered.</param>
    /// <param name="gate">What was inherited from above, or null for unrestricted.</param>
    /// <param name="everyone">True while every ancestor has been world-traversable.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The gate for what is inside, and whether it is still world-reachable.</returns>
    private async Task<(IReadOnlySet<string>? Gate, bool Everyone)> NarrowAsync(
        HdfsFileStatus directory,
        IReadOnlySet<string>? gate,
        bool everyone,
        CancellationToken cancellationToken)
    {
        HdfsAclStatus? acl = await this.hdfs.AclAsync(directory.Path, cancellationToken);

        (IReadOnlyList<string> traversers, bool worldTraversable) =
            HdfsAclBuilder.TraverseGroups(directory, acl);

        if (worldTraversable)
        {
            // Everybody can get through here, so this directory takes nobody
            // out of the running.
            return (gate, everyone);
        }

        var here = new HashSet<string>(traversers, StringComparer.OrdinalIgnoreCase);

        if (gate is not null)
        {
            here.IntersectWith(gate);
        }

        return (here, false);
    }

    /// <summary>
    /// Walks one directory, carrying down who can still reach what is inside it.
    ///
    /// Reading a file on HDFS needs read on the file and execute on every
    /// directory above it, so the set of groups that can reach a file is the
    /// INTERSECTION of the groups that can traverse each of its ancestors. That
    /// set can only be built on the way down, which is why it is threaded
    /// through the walk rather than recomputed per file: one GETACLSTATUS per
    /// directory, not per file.
    ///
    /// A null gate means no ancestor has restricted anybody yet. It is not the
    /// same as an empty one, and the two must never be conflated - empty means
    /// nobody gets in.
    /// </summary>
    /// <param name="path">The directory to walk.</param>
    /// <param name="found">Files in scope, appended to.</param>
    /// <param name="visited">Directories already walked.</param>
    /// <param name="gate">Groups that can traverse everything above here, or null for unrestricted.</param>
    /// <param name="everyone">True while every ancestor has been world-traversable.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    private async Task WalkAsync(
        string path,
        List<HdfsFileStatus> found,
        HashSet<string> visited,
        IReadOnlySet<string>? gate,
        bool everyone,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(path))
        {
            // HDFS has no symlink loops in practice, but a root list containing
            // both /data and /data/contracts would otherwise walk the second
            // twice and count every file in it twice.
            return;
        }

        IReadOnlyList<HdfsFileStatus> entries;

        try
        {
            entries = await this.hdfs.ListAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            this.log.Warning("{Path} does not exist and was skipped.", path);
            return;
        }

        foreach (HdfsFileStatus entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                (IReadOnlySet<string>? narrowed, bool stillEveryone) =
                    await this.NarrowAsync(entry, gate, everyone, cancellationToken);

                await this.WalkAsync(entry.Path, found, visited, narrowed, stillEveryone, cancellationToken);
                continue;
            }

            if (this.IsIndexable(entry))
            {
                found.Add(entry);
                this.reachable[entry.Path] = (gate, everyone);
            }
        }
    }

    private bool IsIndexable(HdfsFileStatus file)
    {
        string name = file.Path[(file.Path.LastIndexOf('/') + 1)..];

        // Hadoop's own litter: in-progress writes, Hive staging directories and
        // the _SUCCESS marker a job leaves behind. None is a document.
        if (name.StartsWith('.') || name.StartsWith('_') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (this.settings.IncludeExtensions.Count == 0)
        {
            return this.extractors.CanExtract(name);
        }

        string extension = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

        return this.settings.IncludeExtensions.Contains(extension);
    }

    /// <summary>Turns one file into an item, or null when it must not be indexed.</summary>
    private async Task<PushItem?> MapAsync(HdfsFileStatus file, CancellationToken cancellationToken)
    {
        this.examined++;

        // A file deleted between the listing and now does not surface here:
        // WebHdfsClient answers a 404 on GETACLSTATUS with null rather than
        // throwing, and null is also what a path carrying no extended ACL
        // returns. The two are not distinguishable at this point and do not need
        // to be - the listing's POSIX group still says who may read the file,
        // and a file that has really gone is caught at the open below.
        HdfsAclStatus? acl = await this.hdfs.AclAsync(file.Path, cancellationToken);

        RoutingDecision decision = this.routing!.EvaluatePath(file.Path);

        if (!decision.MayIndex)
        {
            this.skipped++;
            return null;
        }

        // What the directories above this file let through. A file the walk did
        // not record is gated shut rather than let through on the assumption it
        // was reachable: an empty gate costs an item, a missing check costs a
        // leak.
        (IReadOnlySet<string>? gate, bool everyone) =
            this.reachable.TryGetValue(file.Path, out (IReadOnlySet<string>? Gate, bool Everyone) found)
                ? found
                : (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        IReadOnlyList<PushAclEntry> grants =
            await this.acls.BuildAsync(file, acl, decision.Groups, cancellationToken, gate, everyone);

        if (grants.Count == 0)
        {
            // Not written. The engine would refuse it anyway; refusing here
            // saves the extraction, which is the expensive part.
            this.skipped++;
            this.log.Warning(
                "{Path} resolves to no Entra group and is not indexed. Its cluster groups were: {Groups}",
                file.Path,
                string.Join(", ", HdfsAclBuilder.ClusterGroups(file, acl, decision.Groups)));
            return null;
        }

        string name = file.Path[(file.Path.LastIndexOf('/') + 1)..];

        ExtractionResult? extraction = await this.extractors.ExtractAsync(
            token => this.OpenOrGoneAsync(file.Path, token),
            name,
            file.Length,
            this.settings.MaxRawFileBytes,
            cancellationToken);

        if (extraction is null)
        {
            // The file was deleted between the listing and the read. That is
            // routine in a live lake - one retention job must not end a crawl of
            // a million files - and it is not an extraction failure either:
            // there is no file left to have failed, so it does not spend the
            // error budget. Skipped rather than indexed, which is the closed
            // choice: an item with no body and no file behind it is a search
            // result that leads nowhere.
            this.skipped++;

            this.log.Information(
                "{Path} was deleted between the listing and the read, and is not indexed this run.",
                file.Path);

            return null;
        }

        if (extraction.Status is ExtractionStatus.Failed)
        {
            this.failures++;
        }

        var item = new PushItem
        {
            Id = ItemId(file.Path),
            ItemType = "file",
            Acl = grants,

            // A file whose text could not be extracted is still indexed, by
            // name, path, owner and date, with a property saying why there is no
            // body. A document nobody can find is worse than a document found
            // without its contents.
            Content = extraction.HasText ? extraction.Text : string.Empty,
        };

        item.AddIfPresent("title", name);
        item.AddIfPresent("fileName", name);
        item.AddIfPresent("fileExtension", System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant());
        item.AddIfPresent("itemPath", file.Path);
        item.AddIfPresent("directoryPath", file.Path[..Math.Max(1, file.Path.LastIndexOf('/'))]);
        item.AddIfPresent("ownerName", file.Owner);
        item.AddIfPresent("groupName", file.Group);
        item.AddIfPresent("sizeBytes", file.Length);
        item.AddIfPresent("modifiedUtc", file.ModifiedUtc.UtcDateTime.ToString("o"));
        item.AddIfPresent("extractStatus", extraction.Status.ToString());

        return item;
    }

    /// <summary>Opens a file, or returns null when it is no longer there.</summary>
    private async Task<Stream?> OpenOrGoneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await this.hdfs.OpenAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Only a 404, and only here. Every other way an open can fail - a
            // 500 from a sick DataNode, a socket that died mid-crawl - stays an
            // extraction failure, so it counts against the error budget instead
            // of being quietly counted as a file somebody deleted.
            return null;
        }
    }

    private void CheckErrorBudget()
    {
        if (this.settings.MaxErrorRatePercent <= 0 || this.examined < 50)
        {
            // Below a sample worth judging, one bad file is 100% and would abort
            // a run that is perfectly healthy.
            return;
        }

        int rate = this.failures * 100 / this.examined;

        if (rate > this.settings.MaxErrorRatePercent)
        {
            throw new InvalidOperationException(
                $"{this.failures} of {this.examined} file(s) examined failed to read or extract ({rate}%), " +
                $"above Settings:MaxErrorRatePercent of {this.settings.MaxErrorRatePercent}. The run stops " +
                "rather than reporting a successful crawl that was mostly skips. The watermark has not moved " +
                "past the last item that was written.");
        }
    }

    private void FlushMarker()
    {
        if (this.pendingMarkerTime.Length == 0)
        {
            return;
        }

        CrawlCheckpoint stored = this.checkpoints.Read();

        var pending = new CrawlCheckpoint
        {
            MarkerTime = this.pendingMarkerTime,
            MarkerKey = this.pendingMarkerKey,
        };

        // Monotonic, with no slack. A marker that can regress is not a
        // high-water mark and carries no invariant at all: a full recrawl reads
        // oldest-first, so a run of it truncated by the per-run cap would
        // otherwise write an early position over a later one and lose every file
        // between them for ever. Slack belongs to the decision about what to
        // READ - it absorbs clock skew against the NameNode - and using it here
        // would licence the marker to move backwards by exactly that much.
        if (!stored.IsAfter(pending.MarkerTimestamp(), pending.MarkerKey, slack: 0))
        {
            // Counted as flushed even though nothing was written: a recrawl
            // working through files behind the high-water mark would otherwise
            // re-read the checkpoint on every single commit for the rest of it.
            this.commitsSinceFlush = 0;
            return;
        }

        stored.MarkerTime = pending.MarkerTime;
        stored.MarkerKey = pending.MarkerKey;
        this.checkpoints.Write(stored);
        this.commitsSinceFlush = 0;
    }

    /// <summary>
    /// A deterministic item ID for a path.
    ///
    /// Graph allows 128 ASCII alphanumeric characters, and an HDFS path is
    /// neither bounded by that nor alphanumeric, so the path is hashed. Being
    /// deterministic is what makes the write an idempotent upsert: the same file
    /// re-read after an interruption updates its item rather than adding a
    /// second one. The real path travels in a property, where it is searchable.
    /// </summary>
    /// <param name="path">The absolute HDFS path.</param>
    /// <returns>The item ID.</returns>
    public static string ItemId(string path)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));

        return "h" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
