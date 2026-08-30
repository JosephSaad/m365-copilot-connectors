// ---------------------------------------------------------------------------
// RecordingCrawlStateStore.cs
// An ICrawlStateStore for tests that need to observe what the engine asked the
// store, rather than what the store did.
//
// Every member delegates to NullCrawlStateStore, which already implements the
// whole seam as a no-op, so this class carries only the behaviour a test wants
// to control. That matters more than it looks: the seam is sixteen members, and
// a fake that reimplemented all of them would drift from the real no-op the
// moment either changed, and would do so silently.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using PushCore;
    using PushCore.State;

    public sealed class RecordingCrawlStateStore : ICrawlStateStore
    {
        private readonly ICrawlStateStore inner = NullCrawlStateStore.Instance;

        /// <summary>What CheckHashVersionAsync will answer.</summary>
        public bool HashVersionChanged { get; set; }

        /// <summary>True once the engine asked about the hash version.</summary>
        public bool HashVersionChecked { get; private set; }

        /// <summary>The version the engine reported, so a test can pin it.</summary>
        public int? CheckedHashVersion { get; private set; }

        /// <summary>The mode the engine asked to open the run in.</summary>
        public CrawlMode? RequestedMode { get; private set; }

        public bool IsEnabled => true;

        public Task<bool> CheckHashVersionAsync(
            string connectionId, int hashVersion, CancellationToken cancellationToken)
        {
            this.HashVersionChecked = true;
            this.CheckedHashVersion = hashVersion;
            return Task.FromResult(this.HashVersionChanged);
        }

        public Task<CrawlRunStart> BeginRunAsync(
            CrawlConnectionInfo connection,
            CrawlMode requested,
            bool dryRun,
            int fullEveryHours,
            CancellationToken cancellationToken)
        {
            // The whole point of this fake. The engine may escalate before it
            // gets here, and the mode it ARRIVES with is the observable - the
            // store's own escalation is a separate rule tested against the
            // procedure, not against this.
            this.RequestedMode = requested;

            return Task.FromResult(new CrawlRunStart(1, requested, requested == CrawlMode.Full, null, 0));
        }

        public Task<IReadOnlyDictionary<string, CrawlItemState>> GetItemStatesAsync(
            IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
            this.inner.GetItemStatesAsync(itemIds, cancellationToken);

        public Task RecordWrittenAsync(
            IReadOnlyCollection<CrawlItemState> items, CancellationToken cancellationToken) =>
            this.inner.RecordWrittenAsync(items, cancellationToken);

        public Task RecordUnchangedAsync(
            IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
            this.inner.RecordUnchangedAsync(itemIds, cancellationToken);

        public Task<IReadOnlyList<CrawlDeletion>> GetPendingDeletesAsync(
            double maxDeletePercent, bool overrideGuard, CancellationToken cancellationToken) =>
            this.inner.GetPendingDeletesAsync(maxDeletePercent, overrideGuard, cancellationToken);

        public Task<IReadOnlyList<string>> GetLiveItemIdsAsync(CancellationToken cancellationToken) =>
            this.inner.GetLiveItemIdsAsync(cancellationToken);

        public Task<IReadOnlySet<string>> CompareAndSeeAsync(
            IReadOnlyCollection<CrawlItemState> candidates, CancellationToken cancellationToken) =>
            this.inner.CompareAndSeeAsync(candidates, cancellationToken);

        public Task ConfirmDeletesAsync(
            IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
            this.inner.ConfirmDeletesAsync(itemIds, cancellationToken);

        public Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken) =>
            this.inner.GetCheckpointAsync(cancellationToken);

        public Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken) =>
            this.inner.SaveCheckpointAsync(marker, cancellationToken);

        public Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
            string sourceType,
            IReadOnlyCollection<string> sourceKeys,
            CancellationToken cancellationToken) =>
            this.inner.ResolvePrincipalsAsync(sourceType, sourceKeys, cancellationToken);

        public Task CachePrincipalAsync(
            PrincipalGrant grant,
            string sourceType,
            TimeSpan? ttl,
            CancellationToken cancellationToken) =>
            this.inner.CachePrincipalAsync(grant, sourceType, ttl, cancellationToken);

        public void RecordThrottle(ThrottleEvent throttle) =>
            this.inner.RecordThrottle(throttle);

        public Task CompleteRunAsync(
            RunTotals totals,
            IReadOnlyCollection<ItemTypeTotals> byType,
            PushTiming timing,
            CancellationToken cancellationToken) =>
            this.inner.CompleteRunAsync(totals, byType, timing, cancellationToken);

        public Task FailRunAsync(
            string errorKind,
            string errorMessage,
            RunTotals totals,
            IReadOnlyCollection<ItemTypeTotals> byType,
            PushTiming timing,
            CancellationToken cancellationToken) =>
            this.inner.FailRunAsync(errorKind, errorMessage, totals, byType, timing, cancellationToken);

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }
}
