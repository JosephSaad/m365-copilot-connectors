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
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hdfs;

using System.Runtime.CompilerServices;
using CdpConnector.Extraction;
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

    private RoutingEvaluator? routing;
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
                this.log.Warning(
                    "Stopping at Settings:MaxItemsPerRun ({Cap}). {Remaining} file(s) in scope were not read " +
                    "this run; the watermark advances only over what was written, so the next run continues " +
                    "from there.",
                    this.settings.MaxItemsPerRun,
                    candidates.Count - yielded);
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
        // Reached only when the enumeration ended and every write returned, so
        // this is the one place the run counter may advance. A failed run leaves
        // the counter alone, which means the full-recrawl cadence counts
        // successful crawls rather than attempts.
        this.FlushMarker();

        CrawlCheckpoint stored = this.checkpoints.Read();
        stored.RunCount = this.checkpoint.RunCount + 1;
        stored.LastCompletedUtc = DateTimeOffset.UtcNow.ToString("o");
        this.checkpoints.Write(stored);

        this.log.Information(
            "Crawl complete. {Examined} file(s) examined, {Failures} failed extraction or read, " +
            "watermark at {Marker}.",
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
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            await this.WalkAsync(root, found, visited, cancellationToken);
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

    private async Task WalkAsync(
        string path,
        List<HdfsFileStatus> found,
        HashSet<string> visited,
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
                await this.WalkAsync(entry.Path, found, visited, cancellationToken);
                continue;
            }

            if (this.IsIndexable(entry))
            {
                found.Add(entry);
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

        HdfsAclStatus? acl;

        try
        {
            acl = await this.hdfs.AclAsync(file.Path, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Deleted between the listing and now.
            this.skipped++;
            return null;
        }

        RoutingDecision decision = this.routing!.EvaluatePath(file.Path);

        if (!decision.MayIndex)
        {
            this.skipped++;
            return null;
        }

        IReadOnlyList<PushAclEntry> grants =
            await this.acls.BuildAsync(file, acl, decision.Groups, cancellationToken);

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

        ExtractionResult extraction = await this.extractors.ExtractAsync(
            token => this.hdfs.OpenAsync(file.Path, token),
            name,
            file.Length,
            this.settings.MaxRawFileBytes,
            cancellationToken);

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
        stored.MarkerTime = this.pendingMarkerTime;
        stored.MarkerKey = this.pendingMarkerKey;
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
