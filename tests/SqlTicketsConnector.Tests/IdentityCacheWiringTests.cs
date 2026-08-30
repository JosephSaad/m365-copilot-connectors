// ---------------------------------------------------------------------------
// IdentityCacheWiringTests.cs
// The identity cache is now called, and these are the tests that say so.
//
// crawl.PrincipalMap, uspResolvePrincipals, uspCachePrincipal and the two store
// methods over them shipped before anything called any of it - the state of
// affairs docs/GO-LIVE-READINESS.md section 4 recorded as "the principal cache
// table and store methods exist, but nothing calls them". PrincipalResolver is
// the caller, and a caller is exactly the kind of thing that can be deleted, or
// quietly stop being reached, without a single existing test noticing: every
// assertion about ACLs in this suite is about which GROUPS come out, and those
// are identical whether the answer took a directory lookup or came off a cached
// row. That is what this file is for.
//
// FIVE CLAIMS, and each one fails differently if the wiring is removed:
//
//   A cached answer costs no directory call. The point of the feature. Asserted
//   as a count of lookups against a Graph adapter, not as an elapsed time.
//
//   A miss is written back. Without this the cache is read-only, always empty,
//   and every run pays for every group for ever while looking like it does not.
//
//   A negative answer is stored as a negative AND is served as one. The half
//   that costs the most to get wrong in both directions: not storing it means an
//   unresolvable group is looked up per run for ever, and serving it badly means
//   a group that exists now stays invisible.
//
//   With no store - or a store that throws - the resolver behaves exactly as it
//   did before any of this existed. This is the "safe degradation" property
//   section 2 of the readiness document treats as shipped behaviour, so it is
//   asserted against a store that FAILS when touched rather than one that
//   politely returns nothing.
//
//   Configuration outranks the cache. Settings:EntraGroupMap is read first and
//   is never written to the store, so an operator's edit takes effect on the
//   next run rather than when a row expires.
//
// WHAT THE FAKES ARE, and why they are not simpler. The directory is a real
// GraphServiceClient over a counting IRequestAdapter, because the assertion that
// matters is "the lookup did not happen" and only the real client can be asked
// that honestly - a hand-rolled resolver seam would count calls to itself. The
// store is a fake rather than a database: everything this file asserts about
// SQL is about which calls are made with which arguments, and what SQL Server
// then does with a TTL is sql/33's business and is verified there against the
// real procedure.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using CdpGraphPush;
    using Microsoft.Graph;
    using Microsoft.Graph.Models;
    using Microsoft.Kiota.Abstractions;
    using Microsoft.Kiota.Abstractions.Serialization;
    using Microsoft.Kiota.Abstractions.Store;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class IdentityCacheWiringTests
    {
        /// <summary>A cluster group name, as HDFS and Ranger would give it.</summary>
        private const string Analysts = "hadoop-analysts";

        /// <summary>A second one, for the tests that need two.</summary>
        private const string Auditors = "hadoop-auditors";

        /// <summary>What the directory answers with when it knows the group.</summary>
        private static readonly Guid FromDirectory = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        /// <summary>A different one, so "which answer won" is always visible.</summary>
        private static readonly Guid FromConfiguration = new Guid("11111111-2222-3333-4444-555555555555");

        [Fact]
        public async Task A_group_the_store_already_knows_is_never_looked_up_in_the_directory()
        {
            // The claim the whole feature rests on. The store answers, so Entra
            // is not asked - and the adapter is primed with a DIFFERENT object ID
            // so that a resolver ignoring the cache does not merely make an extra
            // call, it returns the other answer. Either failure is visible.
            var store = new FakePrincipalStore();
            store.Seed(Analysts, FromDirectory);

            var directory = new CountingDirectory(new Guid("99999999-9999-9999-9999-999999999999"));

            PrincipalResolver resolver = Resolver(store, directory);

            List<PushAclEntry> grants = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            PushAclEntry only = Assert.Single(grants);
            Assert.Equal(FromDirectory.ToString("D"), only.Value);
            Assert.Equal(0, directory.Lookups);

            // Asked once, for the one name, under the one source type. The batch
            // shape is asserted rather than assumed: ICrawlStateStore's own header
            // requires that anything which could be per item is per collection,
            // and a resolver that asked name by name would pass every other
            // assertion in this file.
            List<string> asked = Assert.Single(store.Reads);
            Assert.Equal(new[] { Analysts }, asked.ToArray());
            Assert.Equal(new[] { "AdGroup" }, store.ReadTypes.Distinct().ToArray());

            // Nothing was written. A read that hits must not rewrite the row it
            // just read - that would be a SQL round trip per group per run spent
            // to move ExpiresUtc, on the path that exists to avoid round trips.
            Assert.Empty(store.Writes);
        }

        [Fact]
        public async Task A_group_the_store_has_never_seen_is_looked_up_once_and_written_back()
        {
            // The other half. A miss must reach the directory, and the answer must
            // land in crawl.PrincipalMap - a cache that only ever reads is a table
            // that stays empty while every run pays full price and reports nothing
            // unusual.
            var store = new FakePrincipalStore();
            var directory = new CountingDirectory(FromDirectory);

            PrincipalResolver resolver = Resolver(store, directory);

            // Twice, as two files naming the same group would. The second call is
            // what proves the in-memory level is still in front of the store: it
            // must produce the same grant without a second read and without a
            // second write.
            List<PushAclEntry> first = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);
            List<PushAclEntry> second = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(first).Value);
            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(second).Value);

            Assert.Equal(1, directory.Lookups);
            Assert.Single(store.Reads);

            CachedPrincipal written = Assert.Single(store.Writes);
            Assert.Equal(Analysts, written.Grant.SourceKey);
            Assert.Equal(FromDirectory, written.Grant.EntraObjectId);
            Assert.Equal("group", written.Grant.EntraType);
            Assert.Equal("AdGroup", written.SourceType);

            // The TTL sent is the schema's own default. What the database then
            // does with it - honour it for a positive answer, clamp it for a
            // negative one - is sql/33's rule, proven there against the real
            // procedure. What is proven HERE is that a number is sent at all and
            // that it is the one the connection's policy column defaults to, so
            // the caller and the schema do not disagree out of the box.
            Assert.Equal(TimeSpan.FromMinutes(720), written.Ttl);
            Assert.Equal(PrincipalResolver.DefaultCacheTtl, written.Ttl);
        }

        [Fact]
        public async Task A_group_the_directory_does_not_know_is_written_back_as_a_negative()
        {
            // A negative answer is an answer and is stored as one: the row exists,
            // the object ID is null. Discarding it instead is what makes an
            // unresolvable cluster group cost a directory lookup on every item of
            // every run for ever, which is the single most expensive thing this
            // resolver can do.
            var store = new FakePrincipalStore();
            var directory = new CountingDirectory(matches: Array.Empty<Guid>());

            PrincipalResolver resolver = Resolver(store, directory);

            List<PushAclEntry> grants = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            Assert.Empty(grants);
            Assert.Equal(1, directory.Lookups);

            CachedPrincipal written = Assert.Single(store.Writes);
            Assert.Equal(Analysts, written.Grant.SourceKey);
            Assert.Null(written.Grant.EntraObjectId);
            Assert.Null(written.Grant.EntraType);

            // Sent at the same TTL as a positive answer, deliberately. The shorter
            // life of a negative entry is crawl.Connection.PrincipalNegativeTtl-
            // Minutes and sql/33 applies it as min(requested, cap); a second
            // opinion hard-coded here would silently overrule a deployment that
            // had chosen a different one, which is the convention sql/33 exists to
            // have removed.
            Assert.Equal(PrincipalResolver.DefaultCacheTtl, written.Ttl);

            // And it is reported. This is not decoration: the run's log and
            // PrincipalResolver.Unresolved are the only places an operator learns
            // that a group grants nothing.
            Assert.Equal(new[] { Analysts }, resolver.Unresolved.ToArray());
        }

        [Fact]
        public async Task A_stored_negative_is_served_as_a_negative_and_is_still_reported()
        {
            // The next run. The stored null must be read back as "the answer is
            // nothing" rather than as "no row, go and ask" - conflating an absent
            // key with a null value would turn every cached negative straight back
            // into a directory lookup and quietly undo the expensive half of the
            // feature while every test about grants still passed.
            var store = new FakePrincipalStore();
            store.Seed(Analysts, objectId: null);

            var directory = new CountingDirectory(FromDirectory);

            PrincipalResolver resolver = Resolver(store, directory);

            List<PushAclEntry> grants = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            Assert.Empty(grants);
            Assert.Equal(0, directory.Lookups);
            Assert.Empty(store.Writes);

            // THE ONE THAT WOULD HAVE BEEN LOST. The warning and this collection
            // used to hang off the lookup, and a negative served from the store
            // never reaches a lookup - so wiring the cache in without moving them
            // would have silenced the report for exactly the groups whose absence
            // had just been made to last an hour instead of a run.
            Assert.Equal(new[] { Analysts }, resolver.Unresolved.ToArray());
        }

        [Fact]
        public async Task With_no_store_the_resolver_resolves_in_memory_exactly_as_it_did_before()
        {
            // Safe degradation, and asserted against a store that FAILS when
            // touched rather than one that returns nothing politely. A resolver
            // that called a disabled store and interpreted the empty answer would
            // pass a test written the other way round, and would be spending a
            // round trip per group on a deployment that configured no database at
            // all.
            var hostile = new FakePrincipalStore { Enabled = false, ThrowOnRead = true, ThrowOnWrite = true };
            var directory = new CountingDirectory(FromDirectory);

            PrincipalResolver disabled = Resolver(hostile, directory);

            Assert.Equal(
                FromDirectory.ToString("D"),
                Assert.Single(await disabled.ResolveAsync(new[] { Analysts }, CancellationToken.None)).Value);

            Assert.Equal(
                FromDirectory.ToString("D"),
                Assert.Single(await disabled.ResolveAsync(new[] { Analysts }, CancellationToken.None)).Value);

            // One lookup for two calls: the run-scoped cache is doing what it
            // always did, which is the entire pre-existing behaviour.
            Assert.Equal(1, directory.Lookups);
            Assert.Empty(hostile.Reads);
            Assert.Empty(hostile.Writes);

            // The same again through the constructor a caller with no state store
            // would actually use - three arguments, no store at all - because the
            // optional parameter is what every existing call site relies on.
            var second = new CountingDirectory(FromDirectory);
            var none = new PrincipalResolver(new Dictionary<string, string>(), second.Client, Logger.None);

            Assert.Equal(
                FromDirectory.ToString("D"),
                Assert.Single(await none.ResolveAsync(new[] { Analysts }, CancellationToken.None)).Value);

            Assert.Equal(1, second.Lookups);
        }

        [Fact]
        public async Task A_store_that_throws_costs_lookups_rather_than_the_run()
        {
            // A cache is not a source of truth: the explicit map and the directory
            // are, and both are still consulted. Ending a nine-hour crawl because
            // a cache read timed out would trade a correct slower run for no run,
            // so the failure is reported and the store is dropped for the rest of
            // the run.
            var store = new FakePrincipalStore { ThrowOnRead = true };
            var directory = new CountingDirectory(FromDirectory);

            PrincipalResolver resolver = Resolver(store, directory);

            List<PushAclEntry> grants = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(grants).Value);
            Assert.Equal(1, directory.Lookups);

            // The write is not attempted after a failed read, and neither is a
            // second read for a second group. One failure per run, not one per
            // group: a dead database would otherwise put the same stack trace in
            // the log once for every group in the cluster.
            await resolver.ResolveAsync(new[] { Auditors }, CancellationToken.None);

            Assert.Single(store.Reads);
            Assert.Empty(store.Writes);
            Assert.Equal(2, directory.Lookups);
        }

        [Fact]
        public async Task An_explicit_mapping_answers_before_the_cache_and_is_never_written_to_it()
        {
            // Settings:EntraGroupMap is a statement somebody wrote down and put
            // under review, not an observation to be memoised. Reading a stored
            // row first would let a mapping that has since been CHANGED keep
            // answering until the row expired - a configuration file saying one
            // thing while the connector does another, for up to twelve hours - and
            // writing the mapping into the table would create exactly that row.
            var store = new FakePrincipalStore();
            store.Seed(Analysts, FromDirectory);

            var directory = new CountingDirectory(FromDirectory);

            var resolver = new PrincipalResolver(
                new Dictionary<string, string> { [Analysts] = FromConfiguration.ToString("D") },
                directory.Client,
                Logger.None,
                store,
                PrincipalResolver.DefaultCacheTtl);

            List<PushAclEntry> grants = await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None);

            Assert.Equal(FromConfiguration.ToString("D"), Assert.Single(grants).Value);
            Assert.Equal(0, directory.Lookups);
            Assert.Empty(store.Writes);

            // Not even asked about. Paying a round trip for a name the
            // configuration already answers is paying for the chance to be misled
            // by the reply.
            Assert.DoesNotContain(store.Reads.SelectMany(read => read), name => name == Analysts);
        }

        [Fact]
        public async Task With_directory_lookups_off_the_persistent_cache_is_not_consulted_at_all()
        {
            // The invariant that keeps the table readable: a row in
            // crawl.PrincipalMap is a recorded answer FROM ENTRA, reachable only
            // on the path that would otherwise ask Entra again. With
            // Settings:ResolveGroupsFromDirectory off there is no such path, and
            // serving a row written when it was on would have the connector grant
            // access on the strength of a directory it has been told not to ask.
            var store = new FakePrincipalStore();
            store.Seed(Analysts, FromDirectory);

            var resolver = new PrincipalResolver(
                new Dictionary<string, string>(),
                graph: null,
                Logger.None,
                store,
                PrincipalResolver.DefaultCacheTtl);

            Assert.Empty(await resolver.ResolveAsync(new[] { Analysts }, CancellationToken.None));

            Assert.Empty(store.Reads);
            Assert.Empty(store.Writes);
            Assert.Equal(new[] { Analysts }, resolver.Unresolved.ToArray());
        }

        [Fact]
        public async Task The_cache_is_reached_through_the_acl_builder_a_crawl_actually_uses()
        {
            // Everything above drives the resolver directly. This one goes the way
            // a file does - HdfsAclBuilder, the mode bits, the owning group - so
            // that the cache cannot be wired to a method the crawl does not call.
            // Two files under one directory, owned by one group, is the shape the
            // whole feature exists for: at a million files it is one lookup and
            // one cached row rather than a million of either.
            var store = new FakePrincipalStore();
            var directory = new CountingDirectory(FromDirectory);

            var builder = new HdfsAclBuilder(Resolver(store, directory), string.Empty);

            var first = new HdfsFileStatus { Group = Analysts, Permission = "640" };
            var second = new HdfsFileStatus { Group = Analysts, Permission = "640" };

            IReadOnlyList<PushAclEntry> firstGrants = await builder.BuildAsync(
                first, acl: null, Array.Empty<string>(), CancellationToken.None);

            IReadOnlyList<PushAclEntry> secondGrants = await builder.BuildAsync(
                second, acl: null, Array.Empty<string>(), CancellationToken.None);

            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(firstGrants).Value);
            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(secondGrants).Value);

            Assert.Equal(1, directory.Lookups);
            Assert.Single(store.Reads);
            Assert.Single(store.Writes);
        }

        [Fact]
        public void The_handoff_publishes_the_store_the_host_built_and_nothing_when_there_is_none()
        {
            // CdpCrawlState is how a CDP connector reaches the store PushHost
            // opened, because PushSourceContext does not carry one. The failure it
            // guards against is a connector configured WITHOUT a state store
            // reading one anyway, so the null case is asserted as hard as the
            // other: publishing must overwrite, not skip.
            try
            {
                PushOptions none = TestData.ValidPushOptions();

                Assert.Null(CdpCrawlState.FromSettings(none, Logger.None));
                Assert.False(CdpCrawlState.Current.IsEnabled);

                PushOptions configured = TestData.ValidPushOptions();
                configured.Settings[CrawlStateWiring.ConnectionStringSetting] =
                    "Server=SQL01;Database=ConnectorState;Integrated Security=true";

                ICrawlStateStore built = CdpCrawlState.FromSettings(configured, Logger.None);

                Assert.NotNull(built);
                Assert.True(built.IsEnabled);

                // The same object, not an equivalent one. A second store would be
                // a second connection with no run open, and SqlCrawlStateStore
                // refuses every principal call made before BeginRunAsync.
                Assert.Same(built, CdpCrawlState.Current);

                // And it goes back. A connector whose configuration names no state
                // database must not inherit the one a previous read published.
                Assert.Null(CdpCrawlState.FromSettings(none, Logger.None));
                Assert.False(CdpCrawlState.Current.IsEnabled);
            }
            finally
            {
                // Restored through the production path rather than through a
                // test-only setter, so this leaves the process exactly as it found
                // it whatever order the tests run in.
                CdpCrawlState.FromSettings(TestData.ValidPushOptions(), Logger.None);
            }
        }

        [Fact]
        public async Task A_dry_run_reads_the_cache_and_never_writes_it()
        {
            // THIS TEST GUARDS THE READ, not the write, and saying so matters:
            // it passes with the dry-run guard removed, because a cache HIT
            // never reaches the write path anyway. Its job is the other half -
            // that a dry run still consults the cache and still serves from it.
            // A dry run that resolved principals differently from a real one
            // would stop being a rehearsal of it, and the preview's item counts
            // and skip decisions rest on the ACL it resolves.
            //
            // The no-write claim is carried by the test below, which does fail
            // when the guard is removed. Checked by removing it.
            var store = new FakePrincipalStore();
            store.Seed(Analysts, FromDirectory);

            var directory = new CountingDirectory(FromDirectory);
            var resolver = new PrincipalResolver(
                new Dictionary<string, string>(),
                directory.Client,
                Logger.None,
                store,
                PrincipalResolver.DefaultCacheTtl,
                isDryRun: true);

            List<PushAclEntry> grants = await resolver.ResolveAsync(
                new[] { Analysts }, CancellationToken.None);

            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(grants).Value);
            Assert.Empty(store.Writes);

            Assert.NotEmpty(store.Reads);
        }

        [Fact]
        public async Task A_dry_run_does_not_write_back_a_directory_answer_either()
        {
            // The other write path: a name the cache has never seen, resolved
            // fresh from the directory. A real run remembers it. A dry run must
            // not, or the first preview against a new cluster silently populates
            // the cache it was run to preview.
            var store = new FakePrincipalStore();
            var directory = new CountingDirectory(FromDirectory);

            var resolver = new PrincipalResolver(
                new Dictionary<string, string>(),
                directory.Client,
                Logger.None,
                store,
                PrincipalResolver.DefaultCacheTtl,
                isDryRun: true);

            List<PushAclEntry> grants = await resolver.ResolveAsync(
                new[] { Analysts }, CancellationToken.None);

            Assert.Equal(FromDirectory.ToString("D"), Assert.Single(grants).Value);
            Assert.Equal(1, directory.Lookups);
            Assert.Empty(store.Writes);
        }
        [Fact]
        public void The_configured_ttl_is_read_and_a_useless_one_is_refused()
        {
            // ABSENT IS NULL, meaning "the database decides".
            //
            // This assertion used to require 720 - the schema's own default -
            // reasoning that a caller shipping a DIFFERENT default would put the
            // connector and crawl.Connection.PrincipalTtlMinutes into a
            // disagreement only crawl.vwPrincipalCacheTtl could see. The reason
            // was right and the remedy was the weaker of the two available: it
            // was forced, because CachePrincipalAsync took a non-nullable
            // TimeSpan and the store therefore always sent a number, so matching
            // the schema's default was the closest thing to deferring to it.
            //
            // The seam is nullable now, so the connector can hold no opinion at
            // all - and then there is no second default to disagree with, rather
            // than two that happen to agree today. It also makes lowering the
            // column actually lower it, which the old shape did not: sending a
            // number unconditionally left sql/33's clamp, which only touches
            // negative answers, as the only thing the database controlled.
            PushOptions options = TestData.ValidPushOptions();

            Assert.Null(CdpCrawlState.PrincipalCacheTtl(options));

            options.Settings[CdpCrawlState.PrincipalCacheTtlSetting] = "60";
            Assert.Equal(TimeSpan.FromMinutes(60), CdpCrawlState.PrincipalCacheTtl(options));

            // Zero is not a short cache. SqlCrawlStateStore rounds it up to one
            // minute and the row is written all but expired - a cache that never
            // hits and never says so - which is the same correction sql/23 makes
            // at the other end of the call.
            options.Settings[CdpCrawlState.PrincipalCacheTtlSetting] = "0";
            Assert.Null(CdpCrawlState.PrincipalCacheTtl(options));

            options.Settings[CdpCrawlState.PrincipalCacheTtlSetting] = "-30";
            Assert.Null(CdpCrawlState.PrincipalCacheTtl(options));

            options.Settings[CdpCrawlState.PrincipalCacheTtlSetting] = "soon";
            Assert.Null(CdpCrawlState.PrincipalCacheTtl(options));
        }

        /// <summary>A resolver with no explicit map, over the given store and directory.</summary>
        private static PrincipalResolver Resolver(FakePrincipalStore store, CountingDirectory directory)
        {
            return new PrincipalResolver(
                new Dictionary<string, string>(),
                directory.Client,
                Logger.None,
                store,
                PrincipalResolver.DefaultCacheTtl);
        }

        /// <summary>One call to CachePrincipalAsync, as the fake store saw it.</summary>
        private sealed class CachedPrincipal
        {
            public PrincipalGrant Grant { get; set; }

            public string SourceType { get; set; }

            public TimeSpan? Ttl { get; set; }
        }

        /// <summary>
        /// An ICrawlStateStore that records what was asked of crawl.PrincipalMap
        /// and can be made to fail.
        ///
        /// Every other member delegates to NullCrawlStateStore, which already
        /// implements the whole seam as a no-op - the same arrangement
        /// RecordingCrawlStateStore uses, and for the same reason: a fake that
        /// reimplemented all sixteen members would drift from the real no-op
        /// silently. That existing fake is not extended here because it belongs to
        /// the engine's tests and delegates the two principal methods away, which
        /// is precisely what these tests need to observe.
        /// </summary>
        private sealed class FakePrincipalStore : ICrawlStateStore
        {
            private readonly ICrawlStateStore inner = NullCrawlStateStore.Instance;
            private readonly Dictionary<string, PrincipalGrant> rows =
                new Dictionary<string, PrincipalGrant>(StringComparer.OrdinalIgnoreCase);

            /// <summary>What IsEnabled answers. False is a connector with no state database.</summary>
            public bool Enabled { get; set; } = true;

            /// <summary>Whether a read throws, standing in for a database that has gone away.</summary>
            public bool ThrowOnRead { get; set; }

            /// <summary>Whether a write throws.</summary>
            public bool ThrowOnWrite { get; set; }

            /// <summary>The key lists passed to ResolvePrincipalsAsync, one entry per call.</summary>
            public List<List<string>> Reads { get; } = new List<List<string>>();

            /// <summary>The source type of each read, so the family can be pinned.</summary>
            public List<string> ReadTypes { get; } = new List<string>();

            /// <summary>Every CachePrincipalAsync call, in order.</summary>
            public List<CachedPrincipal> Writes { get; } = new List<CachedPrincipal>();

            public bool IsEnabled => this.Enabled;

            /// <summary>Puts a row in the cache, as a previous run would have.</summary>
            /// <param name="sourceKey">The cluster group name.</param>
            /// <param name="objectId">The Entra object ID, or null for a stored negative.</param>
            public void Seed(string sourceKey, Guid? objectId)
            {
                this.rows[sourceKey] = new PrincipalGrant(
                    sourceKey, objectId, objectId is null ? null : "group");
            }

            public Task<IReadOnlyDictionary<string, PrincipalGrant>> ResolvePrincipalsAsync(
                string sourceType, IReadOnlyCollection<string> sourceKeys, CancellationToken cancellationToken)
            {
                this.Reads.Add(sourceKeys.ToList());
                this.ReadTypes.Add(sourceType);

                if (this.ThrowOnRead)
                {
                    throw new InvalidOperationException("the state database is not answering");
                }

                var found = new Dictionary<string, PrincipalGrant>(StringComparer.OrdinalIgnoreCase);

                foreach (string key in sourceKeys)
                {
                    // Absent keys are simply left out, which is the real store's
                    // contract: a key with no row is a miss, and a row whose
                    // EntraObjectId is null is a negative HIT.
                    if (this.rows.TryGetValue(key, out PrincipalGrant grant))
                    {
                        found[key] = grant;
                    }
                }

                return Task.FromResult<IReadOnlyDictionary<string, PrincipalGrant>>(found);
            }

            public Task CachePrincipalAsync(
                PrincipalGrant grant, string sourceType, TimeSpan? ttl, CancellationToken cancellationToken)
            {
                this.Writes.Add(new CachedPrincipal { Grant = grant, SourceType = sourceType, Ttl = ttl });

                if (this.ThrowOnWrite)
                {
                    throw new InvalidOperationException("the state database is not answering");
                }

                this.rows[grant.SourceKey] = grant;

                return Task.CompletedTask;
            }

            public Task<CrawlRunStart> BeginRunAsync(
                CrawlConnectionInfo connection,
                CrawlMode requested,
                bool dryRun,
                int fullEveryHours,
                CancellationToken cancellationToken) =>
                this.inner.BeginRunAsync(connection, requested, dryRun, fullEveryHours, cancellationToken);

            public Task<bool> CheckHashVersionAsync(
                string connectionId, int hashVersion, CancellationToken cancellationToken) =>
                this.inner.CheckHashVersionAsync(connectionId, hashVersion, cancellationToken);

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

            public Task HeartbeatAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        /// <summary>
        /// A real GraphServiceClient over an adapter that counts group queries and
        /// answers them from a canned list.
        ///
        /// A real client rather than a seam of the resolver's own, because the
        /// assertion these tests turn on is "no lookup happened" - and a resolver
        /// asked to count its own calls is a witness to itself. This also keeps
        /// the filter, the select and the paging shape on the real code path, so a
        /// change that broke the query would fail here rather than in a tenant.
        /// </summary>
        private sealed class CountingDirectory : IRequestAdapter
        {
            private readonly List<Guid> matches;
            private int lookups;

            public CountingDirectory(params Guid[] matches)
            {
                this.matches = matches.ToList();
                this.Client = new GraphServiceClient(this);
            }

            /// <summary>The client to hand the resolver.</summary>
            public GraphServiceClient Client { get; }

            /// <summary>How many directory queries were actually made.</summary>
            public int Lookups => Volatile.Read(ref this.lookups);

            /// <summary>The OData filters seen, so the query itself stays pinned.</summary>
            public List<string> Filters { get; } = new List<string>();

            public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

            public ISerializationWriterFactory SerializationWriterFactory
            {
                get
                {
                    var registry = new SerializationWriterFactoryRegistry();

                    registry.ContentTypeAssociatedFactories["application/json"] =
                        new Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory();

                    return ApiClientBuilder.EnableBackingStoreForSerializationWriterFactory(registry);
                }
            }

            public void EnableBackingStore(IBackingStoreFactory backingStoreFactory)
            {
            }

            public Task<ModelType> SendAsync<ModelType>(
                RequestInformation requestInfo,
                ParsableFactory<ModelType> factory,
                Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
                CancellationToken cancellationToken = default)
                where ModelType : IParsable
            {
                Interlocked.Increment(ref this.lookups);

                string uri = requestInfo.URI.ToString();

                if (uri.Contains("$filter=", StringComparison.OrdinalIgnoreCase))
                {
                    this.Filters.Add(uri);
                }

                var response = new GroupCollectionResponse
                {
                    Value = this.matches.Select(id => new Group { Id = id.ToString("D") }).ToList(),
                };

                return Task.FromResult((ModelType)(object)response);
            }

            public Task<IEnumerable<ModelType>> SendCollectionAsync<ModelType>(
                RequestInformation requestInfo,
                ParsableFactory<ModelType> factory,
                Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
                CancellationToken cancellationToken = default)
                where ModelType : IParsable =>
                throw new NotSupportedException();

            public Task<ModelType> SendPrimitiveAsync<ModelType>(
                RequestInformation requestInfo,
                Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IEnumerable<ModelType>> SendPrimitiveCollectionAsync<ModelType>(
                RequestInformation requestInfo,
                Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task SendNoContentAsync(
                RequestInformation requestInfo,
                Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<T> ConvertToNativeRequestAsync<T>(
                RequestInformation requestInfo, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
