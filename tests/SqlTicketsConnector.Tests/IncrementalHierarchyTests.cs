// ---------------------------------------------------------------------------
// IncrementalHierarchyTests.cs
// The connector half of an incremental crawl: reading the resume marker,
// yielding in the one order a checkpoint can be taken from, and setting
// PushItem.LastModifiedUtc so a checkpoint exists at all.
//
// WHAT WAS BROKEN AND WHAT THESE PIN. The engine has plumbed the resume marker
// and the change-detection tier since crawl state shipped, but no connector
// read either, so crawl.Checkpoint had never held a row across thirty-six runs
// and Settings:Incremental changed nothing but the log. sql/26's view was worse
// than unreached - it projected twelve columns against a thirty-column SELECT,
// so pointing Source:ItemView at it failed on nineteen invalid column names.
//
// Three of the tests below would have caught that at build time: the column
// lists of the two queries are compared to each other, the marker column is
// named rather than assumed, and the incremental query is asserted to order by
// the pair rather than by item type.
//
// WHAT THESE TESTS CANNOT PROVE, said plainly, because this repository has been
// bitten by tests that passed while proving nothing:
//
//   * That dbo.vwExternalItemsIncremental returns the same items as
//     dbo.vwExternalItems, or that its columns exist. No SQL Server here. That
//     is sql/35, which compares a SHA2_256 per row across both views, and it is
//     run against the live corpus rather than asserted.
//   * That SQL Server's ORDER BY collation and the state store's MarkerKey
//     comparison agree. Both are server-side string comparisons; the resume
//     tests below restate the rule in C# and prove the ENGINE honours it, not
//     that T-SQL implements it identically.
//
// What they do prove is the half that lives in this process: the query text,
// the tier and ordering declarations, and that the engine turns a marker on an
// item into a composite checkpoint - and refuses to when a write was refused.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Connector.Security.Configuration;
    using Microsoft.Graph;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using PushCore.Sql;
    using PushCore.State;
    using Serilog.Core;
    using SqlHierarchyPush;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class IncrementalHierarchyTests
    {
        private const string ConnectionId = "consultingwork";

        /// <summary>One instant every scripted row in this file shares.</summary>
        /// <remarks>
        /// Shared on purpose, and it is the realistic case rather than a corner:
        /// sql/26's triggers stamp an entire customer subtree with a single
        /// SYSUTCDATETIME(), so on the live corpus 111,900 of 111,900 items sit
        /// in a tie group, the largest holding 16,743. A checkpoint carrying only
        /// a timestamp would either re-read a group of sixteen thousand for ever
        /// or lose whichever of them had not been written when the run stopped.
        /// </remarks>
        private static readonly DateTime Tied =
            new DateTime(2026, 8, 30, 4, 22, 20, 553, DateTimeKind.Utc);

        private static readonly DateTime Later = Tied.AddMilliseconds(17);

        // -------------------------------------------------------------------
        // The query text
        // -------------------------------------------------------------------

        [Fact]
        public void The_full_read_is_unchanged_when_the_incremental_setting_is_off()
        {
            // The control, and it guards the far more common path. Every
            // deployment today runs with Settings:Incremental off, and the full
            // read is what 111,900 items were indexed by. If adding the second
            // query had moved so much as a column in the first, every one of
            // those items would rehash differently and the next run would rewrite
            // the corpus.
            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            string query = new HierarchyPushConnector().BuildQuery(options);

            Assert.Contains("FROM dbo.vwExternalItems ", query, StringComparison.Ordinal);
            Assert.Contains(
                "ORDER BY CASE ItemType WHEN 'Customer' THEN 0 WHEN 'Engagement' THEN 1 ELSE 2 END, ItemId",
                query,
                StringComparison.Ordinal);

            // No marker, no ceiling, no resume parameters: the full read keeps no
            // position, which is what lets SqlPushSource write with sixteen
            // writers.
            Assert.DoesNotContain("EffectiveLastModified", query, StringComparison.Ordinal);
            Assert.DoesNotContain("@ResumeTime", query, StringComparison.Ordinal);
            Assert.DoesNotContain("@ReadCeiling", query, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_queries_project_exactly_the_same_thirty_columns()
        {
            // THE DRIFT GUARD, and the most valuable assertion in this file.
            //
            // One connection alternates between the two reads for the rest of its
            // life: the engine escalates to a full crawl on a hash-version change
            // and every Settings:FullEveryHours, and falls back to the full read
            // whenever there is no checkpoint. If the two queries selected even
            // slightly different column sets, the item built from one would hash
            // differently from the item built from the other, and every
            // alternation would rewrite the whole corpus while reporting an
            // ordinary successful run. No error, no bad item - just the bill.
            PushOptions options = TestData.ValidPushOptions(ConnectionId);

            string[] full = SelectedColumns(new HierarchyPushConnector().BuildQuery(options));
            string[] incremental = SelectedColumns(HierarchyPushConnector.BuildIncrementalQuery(options, null));

            Assert.Equal(30, full.Length);

            // The incremental read selects the same thirty and exactly one more:
            // the marker, which is the whole difference between the two views.
            Assert.Equal(full, incremental.Take(30).ToArray());
            Assert.Equal(31, incremental.Length);
            Assert.EndsWith(HierarchyPushConnector.MarkerColumn, incremental[30], StringComparison.Ordinal);
        }

        [Fact]
        public void The_incremental_read_orders_by_the_pair_and_caps_the_marker_at_the_read_ceiling()
        {
            PushOptions options = TestData.ValidPushOptions(ConnectionId, HierarchyPushConnector.IncrementalItemView);
            string query = HierarchyPushConnector.BuildIncrementalQuery(options, null);

            Assert.Contains($"FROM {HierarchyPushConnector.IncrementalItemView} ", query, StringComparison.Ordinal);

            // ASCENDING (marker, id), strictly. An out-of-order read does not
            // produce an out-of-order checkpoint - the store refuses to move the
            // marker backwards - it produces a checkpoint sitting at the largest
            // pair the run happened to reach, with unwritten rows below it that
            // the next run starts strictly after and never sees again.
            Assert.Contains("ORDER BY EffectiveLastModified, ItemId ", query, StringComparison.Ordinal);
            Assert.DoesNotContain("ORDER BY CASE ItemType", query, StringComparison.Ordinal);

            // The ceiling is captured ONCE into a variable. SYSUTCDATETIME() is
            // non-deterministic, so evaluating it inline would let the filter and
            // the marker cap disagree row by row - which is exactly the gap the
            // ceiling exists to close.
            Assert.Contains("DECLARE @ReadCeiling DATETIME2(3) = SYSUTCDATETIME();", query, StringComparison.Ordinal);
            Assert.Contains(
                $"CASE WHEN EffectiveLastModified < @ReadCeiling THEN EffectiveLastModified END AS " +
                HierarchyPushConnector.MarkerColumn,
                query,
                StringComparison.Ordinal);

            // A full read must return EVERY live record, because the delete sweep
            // removes whatever a completed full crawl did not return. So the
            // ceiling never appears in the WHERE clause of a read with no marker:
            // it only declines to checkpoint past those rows.
            Assert.DoesNotContain("WHERE", query, StringComparison.Ordinal);
        }

        [Fact]
        public void Resuming_reads_strictly_after_the_composite_pair()
        {
            PushOptions options = TestData.ValidPushOptions(ConnectionId, HierarchyPushConnector.IncrementalItemView);
            string query = HierarchyPushConnector.BuildIncrementalQuery(
                options, new CrawlMarker(Tied, "time5000"));

            // Strictly after the PAIR. ">= the timestamp" re-reads a tie group of
            // sixteen thousand every run for ever; "> the timestamp" loses
            // whichever of them had not been written when the run stopped. Only
            // the pair is both exact and terminating.
            Assert.Contains(
                "WHERE EffectiveLastModified < @ReadCeiling " +
                "AND (EffectiveLastModified > @ResumeTime " +
                "OR (EffectiveLastModified = @ResumeTime AND ItemId > @ResumeKey))",
                query,
                StringComparison.Ordinal);

            // Parameters, never the marker's text. The key is an item ID from the
            // source and the time is a value from another database; interpolating
            // either would be an injection point on the one query that reads a
            // value written by a different process.
            Assert.DoesNotContain("time5000", query, StringComparison.Ordinal);
            Assert.DoesNotContain(Tied.ToString("o", CultureInfo.InvariantCulture), query, StringComparison.Ordinal);

            // The delta spans five orders of magnitude between the first run and
            // the steady state, so a cached plan is the wrong plan for one of
            // them. This query runs once per crawl; the recompile is free.
            Assert.Contains("OPTION (RECOMPILE);", query, StringComparison.Ordinal);
        }

        [Fact]
        public void A_row_cap_still_applies_on_both_reads()
        {
            PushOptions options = TestData.ValidPushOptions(ConnectionId, HierarchyPushConnector.IncrementalItemView);
            options.Source.MaxItems = 25;

            // TOP with an ORDER BY on the pair is a bounded catch-up rather than a
            // truncation: the run reads the first twenty-five in checkpoint order,
            // the checkpoint lands on the last of them, and the next run continues
            // from there. On the FULL read the same setting is a foot-gun, because
            // the delete sweep concludes the unread remainder was deleted - which
            // is why it is documented as a smoke-test setting.
            Assert.Contains("SELECT TOP (25) ", HierarchyPushConnector.BuildIncrementalQuery(options, null), StringComparison.Ordinal);
            Assert.Contains("SELECT TOP (25) ", new HierarchyPushConnector().BuildQuery(options), StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------
        // What the connector hands the engine
        // -------------------------------------------------------------------

        [Fact]
        public void The_setting_chooses_the_source_class_and_with_it_the_whole_contract()
        {
            // Differencing plus concurrent writers, or ChangeMarker plus serial
            // writes. There is no valid mixture: a marker source written several
            // items at a time lets chunk n+1 save its checkpoint before chunk n
            // has landed, and uspSaveCheckpoint's forward-only rule then REFUSES
            // the correction rather than applying it.
            Assert.IsType<SqlPushSource>(SourceFor(incremental: false));

            IPushSource marker = SourceFor(incremental: true);

            Assert.IsType<HierarchyIncrementalSource>(marker);
            Assert.Equal(SourceChangeDetection.ChangeMarker, marker.ChangeDetection);
            Assert.True(marker.RequiresOrderedCommit);

            IPushSource differencing = SourceFor(incremental: false);

            Assert.Equal(SourceChangeDetection.Differencing, differencing.ChangeDetection);
            Assert.False(differencing.RequiresOrderedCommit);
        }

        [Fact]
        public void The_default_view_follows_the_setting()
        {
            IPushConnector connector = new HierarchyPushConnector();

            PushOptions off = TestData.ValidPushOptions(ConnectionId);
            off.Source.ItemView = string.Empty;
            connector.ApplyDefaults(off);

            Assert.Equal("dbo.vwExternalItems", off.Source.ItemView);

            PushOptions on = TestData.ValidPushOptions(ConnectionId);
            on.Source.ItemView = string.Empty;
            on.Settings["Incremental"] = "true";
            connector.ApplyDefaults(on);

            Assert.Equal(HierarchyPushConnector.IncrementalItemView, on.Source.ItemView);

            // A view named in configuration is never overridden. The default is
            // what an existing appsettings.json that predates this feature keeps.
            PushOptions named = TestData.ValidPushOptions(ConnectionId, "dbo.vwSomethingElse");
            named.Settings["Incremental"] = "true";
            connector.ApplyDefaults(named);

            Assert.Equal("dbo.vwSomethingElse", named.Source.ItemView);
        }

        [Fact]
        public void Incremental_against_the_full_view_is_refused_before_anything_opens()
        {
            // The exact configuration this feature was blocked on, caught at exit
            // 2 with a key name instead of at exit 4 with "Invalid column name
            // 'EffectiveLastModified'" - which arrives after the connection and
            // schema have been registered and names a column rather than a
            // setting.
            PushOptions options = TestData.ValidPushOptions(ConnectionId, "dbo.vwExternalItems");
            options.Settings["Incremental"] = "true";

            var errors = new ValidationErrors();
            new HierarchyPushConnector().ValidateOptions(options, errors);

            Assert.True(errors.HasErrors);
            Assert.Contains(
                HierarchyPushConnector.IncrementalItemView,
                string.Join(" ", errors.Errors),
                StringComparison.Ordinal);
            Assert.Contains(
                "Source:ItemView",
                string.Join(" ", errors.Errors),
                StringComparison.Ordinal);

            // And the control: the same view with the setting off is the shipped
            // configuration and must stay valid.
            options.Settings["Incremental"] = "false";
            var clean = new ValidationErrors();
            new HierarchyPushConnector().ValidateOptions(options, clean);

            Assert.False(clean.HasErrors);
        }

        // -------------------------------------------------------------------
        // The checkpoint the engine takes from the marker
        // -------------------------------------------------------------------

        [Fact]
        public async Task The_checkpoint_is_the_pair_from_the_last_confirmed_item()
        {
            // The one thing thirty-six runs of this project never did. Every item
            // shares one timestamp, so the timestamp alone identifies nothing -
            // the key is what says how far the run got inside the group.
            var rows = new List<PushItem>
            {
                Row("cust1", Tied),
                Row("eng10", Tied),
                Row("time100", Tied),
                Row("time101", Later),
            };

            var store = new CheckpointRecordingStore();

            await RunAsync(rows, store);

            Assert.NotEmpty(store.Saved);

            CrawlMarker last = store.Saved[store.Saved.Count - 1];

            Assert.Equal(Later, last.MarkerTime);
            Assert.Equal("time101", last.MarkerKey);
        }

        [Fact]
        public async Task An_item_with_no_marker_cannot_move_the_checkpoint()
        {
            // What the read ceiling produces on a full crawl: a row modified while
            // the crawl was running is still RETURNED, so the delete sweep does not
            // conclude it was deleted, but it carries no marker and the checkpoint
            // stays below it. It is read again on the next run, which is the only
            // safe direction to be wrong in.
            var rows = new List<PushItem>
            {
                Row("cust1", Tied),
                Row("eng10", Tied),
                Row("time999", null),
            };

            var store = new CheckpointRecordingStore();

            await RunAsync(rows, store);

            CrawlMarker last = store.Saved[store.Saved.Count - 1];

            Assert.Equal(Tied, last.MarkerTime);
            Assert.Equal("eng10", last.MarkerKey);
        }

        [Fact]
        public async Task A_refused_write_stops_the_checkpoint_before_the_gap()
        {
            // The rule the whole IPushSource contract exists for, now with a
            // checkpoint actually behind it. Six rows in two chunks of three; the
            // second chunk has a refusal in the middle of it.
            //
            // The first chunk is clean, so the checkpoint advances to its last
            // item. The second leaves a gap, and the engine then saves NOTHING for
            // it - not even the prefix before the refusal. That is stricter than
            // the source's own commit callback, which does get the prefix, and it
            // is the conservative direction: the checkpoint stays at the last pair
            // a whole chunk stands behind, so the next run re-reads from there and
            // retries what was refused. Re-reading what was already written costs
            // time and nothing else, because every write is an upsert.
            var rows = new List<PushItem>
            {
                Row("cust1", Tied),
                Row("eng10", Tied),
                Row("time100", Tied),
                Row("time101", Tied),
                Row("time102", Tied),
                Row("time103", Later),
            };

            var store = new CheckpointRecordingStore();
            StubGraphAdapter adapter = Adapter();

            adapter.BatchStatusFor = id => id == "time102" ? 400 : (int?)null;

            // Chunks of three, because the refusal has to happen INSIDE a chunk
            // for the gap rule to be the thing under test. A chunk of one takes
            // the single-write path, where a refusal throws and ends the run - a
            // different rule, covered elsewhere.
            await RunAsync(rows, store, adapter, batch: true, chunkSize: 3);

            // Exactly one save: the clean chunk's. The chunk with the gap in it
            // never moved the marker at all.
            Assert.Single(store.Saved);
            Assert.Equal("time100", store.Saved[0].MarkerKey);
            Assert.Equal(Tied, store.Saved[0].MarkerTime);

            // The other half of the same fact, and the half a checkpoint-only
            // check would pass through: the item after the gap was still written,
            // because it is genuinely in the index and the sweep must not remove
            // it. Only the marker is held back.
            Assert.Contains("time103", adapter.WrittenItemIds);
            Assert.DoesNotContain("time102", adapter.WrittenItemIds);
        }

        [Fact]
        public async Task A_resume_across_a_tie_boundary_skips_nothing_and_repeats_nothing()
        {
            // Six rows sharing one timestamp, cut in the middle. This is the case
            // that is most likely to be silently wrong, because a timestamp-only
            // marker passes every test written with distinct timestamps and fails
            // here - and on this source EVERY row is in a tie group.
            //
            // The rule restated in C# below is the same one BuildIncrementalQuery
            // emits as T-SQL: strictly after the PAIR. What this proves is that
            // the engine's checkpoint, fed into that rule, partitions the corpus
            // exactly. That the T-SQL implements the same rule is proven live and
            // by sql/35, not here.
            var corpus = new List<PushItem>
            {
                Row("time500", Tied),
                Row("time501", Tied),
                Row("time502", Tied),
                Row("time503", Tied),
                Row("time504", Tied),
                Row("time505", Tied),
                Row("time506", Later),
            };

            // First pass stops after three, exactly as a killed process would.
            var firstStore = new CheckpointRecordingStore();
            var firstAdapter = Adapter();

            await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
                corpus,
                firstStore,
                firstAdapter,
                batch: false,
                failAfter: 3));

            CrawlMarker resume = firstStore.Saved[firstStore.Saved.Count - 1];

            Assert.Equal("time502", resume.MarkerKey);

            // Second pass reads strictly after the pair, as the SQL would.
            List<PushItem> remaining = corpus.Where(item => After(item, resume)).ToList();

            var secondStore = new CheckpointRecordingStore();
            var secondAdapter = Adapter();

            await RunAsync(remaining, secondStore, secondAdapter);

            List<string> firstPass = firstAdapter.WrittenItemIds.ToList();
            List<string> secondPass = secondAdapter.WrittenItemIds.ToList();

            // Nothing repeated.
            Assert.Empty(firstPass.Intersect(secondPass, StringComparer.OrdinalIgnoreCase));

            // Nothing skipped: the two passes together are the whole corpus, and
            // the boundary fell inside the tie group rather than between groups.
            Assert.Equal(
                corpus.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                firstPass.Concat(secondPass).OrderBy(id => id, StringComparer.Ordinal).ToArray());

            Assert.Equal(new[] { "time503", "time504", "time505", "time506" }, secondPass.ToArray());
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>The predicate BuildIncrementalQuery emits, restated for the test.</summary>
        /// <remarks>
        /// Ordinal on the key, because these fixtures are lowercase alphanumeric
        /// item IDs of the shape this connector composes - "cust10482",
        /// "time4410027" - where every collation this source could carry agrees
        /// with ordinal. The live comparison is SQL Server's, in the source
        /// database's collation, and the store's forward-only comparison is SQL
        /// Server's too, in the state database's. Both are
        /// SQL_Latin1_General_CP1_CI_AS on the rig.
        /// </remarks>
        private static bool After(PushItem item, CrawlMarker marker)
        {
            DateTime value = item.LastModifiedUtc.Value;

            return value > marker.MarkerTime ||
                (value == marker.MarkerTime &&
                 string.CompareOrdinal(item.Id, marker.MarkerKey) > 0);
        }

        private static PushItem Row(string id, DateTime? marker)
        {
            return new PushItem
            {
                Id = id,
                ItemType = id.StartsWith("cust", StringComparison.Ordinal) ? "Customer" : "TimeEntry",
                Content = "content for " + id,
                LastModifiedUtc = marker,
            };
        }

        private static StubGraphAdapter Adapter()
        {
            return new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new HierarchyPushConnector().BuildSchema());
        }

        private static IPushSource SourceFor(bool incremental)
        {
            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Incremental"] = incremental ? "true" : "false";

            IPushConnector connector = new HierarchyPushConnector();

            connector.ApplyDefaults(options);

            return connector.CreateSource(new PushSourceContext(
                options, new Azure.Identity.DefaultAzureCredential(), null, Logger.None));
        }

        /// <summary>Pulls the selected column list out of a query, one entry per column.</summary>
        private static string[] SelectedColumns(string query)
        {
            int select = query.IndexOf("SELECT ", StringComparison.Ordinal) + "SELECT ".Length;
            int from = query.IndexOf(" FROM ", select, StringComparison.Ordinal);

            string list = query.Substring(select, from - select);

            // The row cap is part of SELECT and is not a column.
            if (list.StartsWith("TOP (", StringComparison.Ordinal))
            {
                list = list.Substring(list.IndexOf(") ", StringComparison.Ordinal) + 2);
            }

            // The marker is a CASE expression carrying one comma-free alias, so a
            // naive split is safe here and is asserted to be: the marker entry is
            // checked by its AS clause rather than by position alone.
            return list.Split(',').Select(part => part.Trim()).ToArray();
        }

        private static Task RunAsync(List<PushItem> rows, CheckpointRecordingStore store)
        {
            return RunAsync(rows, store, Adapter());
        }

        private static async Task RunAsync(
            List<PushItem> rows,
            CheckpointRecordingStore store,
            StubGraphAdapter adapter,
            bool batch = false,
            int failAfter = 0,
            int chunkSize = 1)
        {
            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Incremental"] = "true";
            options.Settings["Batch"] = batch ? "true" : "false";

            // One row per chunk by default, so a checkpoint is saved per item and
            // the test can see where a killed run stopped rather than only where
            // it ended. The engine saves once per chunk by design - a round trip
            // beside every Graph write would be a database call for a value only
            // the next run reads.
            options.Settings["LookupChunkSize"] = chunkSize.ToString(CultureInfo.InvariantCulture);

            int yielded = 0;

            var source = new FakePushSource(
                rows,
                throwOn: failAfter <= 0
                    ? null
                    : _ => ++yielded > failAfter
                        ? new InvalidOperationException("the source died mid-crawl")
                        : null);

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new GraphServiceClient(adapter),
                Logger.None,
                dryRun: false,
                store);

            await engine.RunAsync(new PushSourceContext(
                options, new Azure.Identity.DefaultAzureCredential(), null, Logger.None));
        }

        /// <summary>Records every checkpoint the engine saved, in order.</summary>
        /// <remarks>
        /// Separate from RecordingCrawlStateStore rather than a change to it,
        /// because that fake is shared with the hash-version tests and its
        /// BeginRunAsync deliberately answers FullCrawlDue for a full request.
        /// This one answers the run the way a store with a baseline and a
        /// checkpoint would, which is the state an incremental run needs to
        /// exist in at all.
        /// </remarks>
        private sealed class CheckpointRecordingStore : ICrawlStateStore
        {
            private readonly ICrawlStateStore inner = NullCrawlStateStore.Instance;

            public List<CrawlMarker> Saved { get; } = new List<CrawlMarker>();

            public bool IsEnabled => true;

            public Task<bool> CheckHashVersionAsync(
                string connectionId, int hashVersion, CancellationToken cancellationToken) =>
                Task.FromResult(false);

            public Task<CrawlRunStart> BeginRunAsync(
                CrawlConnectionInfo connection,
                CrawlMode requested,
                bool dryRun,
                int fullEveryHours,
                CancellationToken cancellationToken) =>
                Task.FromResult(new CrawlRunStart(1, requested, false, DateTime.UtcNow, 0));

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

                NullCrawlStateStore.Instance.CompareAndSeeAsync(candidates, cancellationToken);

            public Task ConfirmDeletesAsync(
                IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
                this.inner.ConfirmDeletesAsync(itemIds, cancellationToken);

            public Task<CrawlMarker?> GetCheckpointAsync(CancellationToken cancellationToken) =>
                this.inner.GetCheckpointAsync(cancellationToken);

            public Task SaveCheckpointAsync(CrawlMarker marker, CancellationToken cancellationToken)
            {
                this.Saved.Add(marker);
                return Task.CompletedTask;
            }

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

            public void RecordThrottle(ThrottleEvent throttle) => this.inner.RecordThrottle(throttle);

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
}
