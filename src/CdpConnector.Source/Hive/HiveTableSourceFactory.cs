// ---------------------------------------------------------------------------
// HiveTableSourceFactory.cs
// The assembly a table connector would otherwise repeat.
//
// Every Hive table connector needs the same five things wired together in the
// same order: read the Ranger policies, decide whether this table may be
// indexed at all, resolve the granted groups to Entra grants, open a reader,
// and hand the row mapping to the source. Doing that in each connector would
// mean five chances per table to get the routing check in the wrong place - and
// the wrong place is "after the query", where the rows a filtered table would
// have hidden have already been read.
//
// So the order lives here once. A connector supplies a mapping and a key.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hive;

using CdpConnector.Source.Acl;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using PushCore;

/// <summary>Builds a source for one Hive or Impala table.</summary>
public sealed class HiveTableSourceFactory
{
    private readonly CdpSettings settings;
    private readonly PrincipalResolver principals;
    private readonly Func<HiveRow, PushOptions, PushItem?> map;

    /// <summary>Initializes a new instance of the <see cref="HiveTableSourceFactory"/> class.</summary>
    /// <param name="settings">Validated CDP settings.</param>
    /// <param name="principals">Turns Ranger's group names into Entra grants.</param>
    /// <param name="map">The connector's row mapping.</param>
    public HiveTableSourceFactory(
        CdpSettings settings, PrincipalResolver principals, Func<HiveRow, PushOptions, PushItem?> map)
    {
        this.settings = settings;
        this.principals = principals;
        this.map = map;
    }

    /// <summary>
    /// A deterministic item ID for one row.
    ///
    /// Graph allows 128 ASCII alphanumeric characters and a natural key is
    /// neither bounded by that nor alphanumeric, so the qualified table name and
    /// the key are hashed together. Including the table means two tables sharing
    /// a key space cannot collide into one item, and being deterministic is what
    /// makes a re-read an update rather than a duplicate.
    /// </summary>
    /// <param name="itemView">The qualified table name.</param>
    /// <param name="key">The row's natural key.</param>
    /// <returns>The item ID.</returns>
    public static string ItemId(string itemView, string key)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(itemView + "" + key));

        return "t" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Assembles the source, in the order that keeps the routing check first.</summary>
    /// <param name="context">Configuration, credential and logger.</param>
    /// <param name="reader">The row reader, normally over ODBC.</param>
    /// <param name="connectorKey">Names the checkpoint file.</param>
    /// <returns>The source.</returns>
    public IPushSource Create(PushSourceContext context, IHiveRowReader reader, string connectorKey)
    {
        var ranger = new RangerPolicyClient(this.settings.RangerBaseUrl, context.Log);

        return new HiveRoutedSource(
            this.settings,
            context,
            reader,
            ranger,
            this.principals,
            new CheckpointStore(this.settings.CheckpointDirectory, connectorKey, context.Log),
            this.map);
    }
}

/// <summary>
/// Defers everything to the first read, so that opening a source cannot make a
/// network call and so the Ranger verdict is computed before the query runs.
/// </summary>
internal sealed class HiveRoutedSource : IPushSource
{
    private readonly CdpSettings settings;
    private readonly PushSourceContext context;
    private readonly IHiveRowReader reader;
    private readonly RangerPolicyClient ranger;
    private readonly PrincipalResolver principals;
    private readonly CheckpointStore checkpoints;
    private readonly Func<HiveRow, PushOptions, PushItem?> map;

    private HivePushSource? inner;

    internal HiveRoutedSource(
        CdpSettings settings,
        PushSourceContext context,
        IHiveRowReader reader,
        RangerPolicyClient ranger,
        PrincipalResolver principals,
        CheckpointStore checkpoints,
        Func<HiveRow, PushOptions, PushItem?> map)
    {
        this.settings = settings;
        this.context = context;
        this.reader = reader;
        this.ranger = ranger;
        this.principals = principals;
        this.checkpoints = checkpoints;
        this.map = map;
    }

    public int Skipped => this.inner?.Skipped ?? 0;

    public async IAsyncEnumerable<PushItem> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<RangerPolicy> policies =
            await this.ranger.PoliciesAsync(this.settings.RangerSqlService, cancellationToken);

        var routing = new RoutingEvaluator(policies);

        (string database, string table) = HivePushSource.SplitTable(this.context.Options.Source.ItemView);

        RoutingDecision decision = routing.EvaluateTable(database, table);

        // Resolved before the query so that a table nobody can be granted is
        // refused without reading a row of it.
        List<PushAclEntry> grants = decision.MayIndex
            ? await this.principals.ResolveAsync(decision.Groups, cancellationToken)
            : [];

        this.inner = new HivePushSource(
            this.settings,
            this.context.Options,
            this.reader,
            routing,
            grants,
            this.checkpoints,
            this.map,
            this.context.Log);

        await foreach (PushItem item in this.inner.ReadAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        return this.inner?.OnItemCommittedAsync(item, cancellationToken) ?? ValueTask.CompletedTask;
    }

    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        return this.inner?.OnCrawlCompletedAsync(cancellationToken) ?? ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        this.ranger.Dispose();
        await this.reader.DisposeAsync();
    }
}
