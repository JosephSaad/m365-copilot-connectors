// ---------------------------------------------------------------------------
// NullCrawlStateStore.cs
// What a connector gets when no ConnectorState database is configured.
//
// Every method is a no-op and IsEnabled is false, which makes the whole state
// store opt-in rather than a new dependency for every existing deployment. A
// connector with no Settings:StateConnectionString behaves exactly as it did
// before any of this existed: it writes every item every run and never deletes.
//
// That is deliberately the PRE-EXISTING behaviour rather than a degraded mode,
// and the distinction matters when reading the engine. The engine branches on
// IsEnabled rather than calling into here and interpreting zeros, because a
// store that returned "no items are on record" is indistinguishable from a real
// store looking at a brand-new connection - and one of those two means "write
// everything", while the other would mean "delete everything" if the sweep ever
// believed it.
//
// GetPendingDeletesAsync therefore returns an empty list rather than throwing.
// It cannot be reached with IsEnabled false, but if it ever is, returning
// nothing to delete is the only safe answer a store with no memory can give.
// ---------------------------------------------------------------------------

namespace PushCore.State;

/// <summary>A store that remembers nothing, for a connector configured without one.</summary>
public sealed class NullCrawlStateStore : ICrawlStateStore
{
    /// <summary>The single instance. It holds no state, so there is no reason for a second.</summary>
    public static readonly NullCrawlStateStore Instance = new NullCrawlStateStore();

    private NullCrawlStateStore()
    {
    }

    /// <inheritdoc/>
    public bool IsEnabled => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Reports run zero and a full crawl, which is the truth: with no memory,
    /// every run reads everything and none of them may conclude anything about
    /// what is missing.
    /// </remarks>
    public Task<CrawlRunStart> BeginRunAsync(
        CrawlConnectionInfo connection,
        CrawlMode requested,
        bool dryRun,
        int fullEveryHours,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrawlRunStart(0, CrawlMode.Full, true, null, 0));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always false. Without a store there are no hashes on record, so there is
    /// no framing to have changed and nothing to migrate - every item is written
    /// on every run already, which is the outcome a version change forces
    /// anyway.
    /// </remarks>
    public Task<bool> CheckHashVersionAsync(
        string connectionId,
        int hashVersion,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, CrawlItemState>> GetItemStatesAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, CrawlItemState> empty =
            new Dictionary<string, CrawlItemState>(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(empty);
    }

    /// <inheritdoc/>
    public Task RecordWrittenAsync(
        IReadOnlyCollection<CrawlItemState> items, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RecordUnchangedAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyList<CrawlDeletion>> GetPendingDeletesAsync(
        double maxDeletePercent, bool overrideGuard, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CrawlDeletion>>(Array.Empty<CrawlDeletion>());

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetLiveItemIdsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <inheritdoc/>
    public Task ConfirmDeletesAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken)
        => Task.FromResult<CrawlMarker?>(null);

    /// <inheritdoc/>
    public Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
        string sourceType, IReadOnlyCollection<string> sourceKeys, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, PrincipalGrant> empty =
            new Dictionary<string, PrincipalGrant>(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(empty);
    }

    /// <inheritdoc/>
    public Task CachePrincipalAsync(
        PrincipalGrant grant, string sourceType, TimeSpan ttl, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public void RecordThrottle(ThrottleEvent throttle)
    {
        // Dropped. PushSummary.ThrottleWaits still counts them for the log line,
        // which is what a deployment without a state store has always had.
    }

    /// <inheritdoc/>
    public Task CompleteRunAsync(
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task FailRunAsync(
        string errorKind,
        string errorMessage,
        RunTotals totals,
        IReadOnlyCollection<ItemTypeTotals> byType,
        PushTiming timing,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
