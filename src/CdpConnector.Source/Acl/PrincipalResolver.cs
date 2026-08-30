// ---------------------------------------------------------------------------
// PrincipalResolver.cs
// A cluster group name in, an Entra group object ID out - or nothing.
//
// "Or nothing" is the whole design. A group this cannot resolve produces no
// grant, which narrows who can see the item. The alternative - carrying on with
// a guess, or falling back to a configured group - would widen the audience of
// exactly the item whose permissions could not be established, which is the one
// item where widening is least defensible.
//
// Two ways to resolve, in order:
//
//   The explicit map. Settings:EntraGroupMap pairs a cluster group name with an
//   Entra group object ID. It needs no Graph permission, it is reviewable in a
//   configuration file, and it is what a regulated deployment should prefer:
//   somebody wrote down that "hadoop-analysts" means this Entra group, and a
//   change to that statement is a change to a file under review.
//
//   The directory lookup, off unless asked for. Where the cluster's Kerberos is
//   AD-integrated, the group names ARE AD group names, and Entra carries them
//   with onPremisesSamAccountName set. That needs an application permission the
//   rest of this connector does not use - GroupMember.Read.All - so it is
//   opt-in rather than a silent new requirement on the app registration.
//
// Results are cached for the run. A crawl of a million files under a hundred
// directories asks about the same dozen groups a million times.
//
// ===========================================================================
// AND, WHEN A CRAWL STATE STORE IS CONFIGURED, ACROSS RUNS.
// ===========================================================================
//
// crawl.PrincipalMap and the two store methods over it - ResolvePrincipalsAsync
// and CachePrincipalAsync - existed before this class called either, which made
// the identity cache a table nothing wrote to and a policy (sql/33) nothing
// obeyed. This is the caller. Six decisions shape it, and each one is a place
// where the obvious implementation is wrong.
//
// 1. THE PERSISTENT CACHE MEMOISES THE DIRECTORY LOOKUP AND NOTHING ELSE.
//
//    The explicit map is consulted BEFORE the store and its answers are never
//    written to it. Both halves matter. An answer that came out of
//    appsettings.json is not a lookup - it costs nothing to re-evaluate - and
//    caching it would mean an operator's edit to Settings:EntraGroupMap does not
//    take effect until a row somewhere expires, which is up to twelve hours of a
//    configuration file that says one thing while the connector does another.
//    Reading the store before the map would be the same trap wearing a different
//    hat: a stale row would shadow the statement an operator just wrote down.
//
//    The same rule is why the store is untouched when directory lookups are off
//    (Settings:ResolveGroupsFromDirectory unset, so `graph` is null). There is
//    nothing to memoise, and reading rows a previous run wrote while lookups
//    WERE on would have the connector grant access on the strength of a
//    directory it has been told not to ask. Turning the directory off takes
//    effect on the next run rather than twelve hours later.
//
//    The upshot is one sentence a reviewer can hold: a row in crawl.PrincipalMap
//    is a recorded answer from Entra, and it is reachable only on the path that
//    would otherwise have asked Entra again.
//
// 2. THE IN-MEMORY CACHE STAYS, IN FRONT OF THE STORE, AND IT IS NOT VESTIGIAL.
//
//    Promoting the run-scoped dictionary INTO the store rather than in front of
//    it would trade one Graph call per group per run for one SQL round trip per
//    group per FILE - a million round trips on the corpus this exists to make
//    possible, charged to a phase nobody is timing. The store is the second
//    level: consulted once per group per run, on the first file that mentions
//    it, and never again. What the dictionary holds is unchanged - resolved ID
//    strings, nulls included - so a negative answer costs one lookup per run
//    exactly as it always did.
//
//    The store read is batched per call rather than issued per name, which is
//    the rule ICrawlStateStore's own header states: every group named by one
//    file that this run has not already seen is asked about in a single call.
//
// 3. NEGATIVE CACHING IS THE RISKY HALF, AND IT IS RISKY IN A NEW WAY.
//
//    Caching "this group resolves to nothing" for the run was already true and
//    was never a problem: a run is minutes long and the answer could not have
//    changed underneath it in any way anybody would notice. Persisting that
//    answer is genuinely different. A group created five minutes before a run
//    that caches its absence stays invisible for the rest of the negative TTL,
//    and every item readable only by that group is pushed without the grant -
//    which, since an item with no grants at all is skipped, means those items
//    are missing from the index rather than merely under-shared. Nothing about
//    that looks like an error; the run reports success.
//
//    That cost buys the case the table exists for. A cluster group with no Entra
//    counterpart - a service account's group, a local group, a name somebody
//    mistyped in a policy years ago - is looked up on every item of every run
//    for ever without it, which is the single most expensive thing an unbounded
//    resolver does. The negative TTL is the price of the ceiling: sql/33 caps a
//    negative answer at crawl.Connection.PrincipalNegativeTtlMinutes, 60 by
//    default, so the exposure is an hour rather than the twelve a positive
//    answer gets. THE DATABASE APPLIES THAT CAP, NOT THIS CLASS. This caller
//    passes one TTL for both kinds of answer and lets uspCachePrincipal take
//    min(requested, cap) on the negative ones - see decision 5 for why not
//    passing a shorter one from here is deliberate.
//
//    What this class owes the operator in exchange is that the answer still gets
//    REPORTED. A negative served from the store returns before any lookup, so
//    the warning that names an unresolved group had to be moved onto the answer
//    rather than onto the lookup, and it says when the answer was a cached one -
//    otherwise "this group does not resolve" is indistinguishable from "this
//    group did not resolve an hour ago", which is precisely the confusion the
//    persistence introduces.
//
// 4. A STALE POSITIVE IS THE ONE THIS CANNOT REPORT, AND IT FAILS CLOSED.
//
//    Twelve hours of a cached mapping is defensible, and the reason is narrower
//    than "groups do not change often". Three things can happen to a resolved
//    group inside the TTL:
//
//      The group is deleted. The cached object ID then grants an item to an
//      object that does not exist, which grants it to nobody. The item is
//      indexed and returned to no one who was not already entitled - narrower
//      than the truth, which is the direction this whole class errs in.
//
//      The group's MEMBERSHIP changes. Irrelevant here, and worth saying because
//      it is the first thing anybody worries about: what is cached is the
//      name-to-object-ID mapping, not the membership. Graph evaluates membership
//      at query time, so somebody removed from the group loses access to every
//      item granted to it immediately, cache or no cache.
//
//      The NAME is freed and reassigned - an AD group deleted and a new one
//      created with the same sAMAccountName inside twelve hours. The cache then
//      grants the OLD group's object ID, which is deleted, which grants nobody;
//      the new group is not granted until the entry expires. Fails closed.
//
//    The case that would be indefensible is a cached object ID that now names a
//    DIFFERENT LIVE GROUP, and that needs Entra to reissue an object ID it has
//    already used. It does not: object IDs are not reused, and a soft-deleted
//    group keeps its ID for the thirty-day restore window. So every staleness
//    mode of a positive entry here under-grants, and none over-grants. Twelve
//    hours is a throughput decision, not a security one - which is exactly why
//    the negative side, where staleness hides a real grant, gets sixty minutes.
//
// 5. THE TTL IS THE SCHEMA'S DEFAULT, AND THE STORE SEAM CANNOT SAY "POLICY".
//
//    DefaultCacheTtl is 720 minutes because crawl.Connection.PrincipalTtlMinutes
//    defaults to 720, so out of the box the caller and the database agree and
//    there is nothing to reconcile.
//
//    They can be made to disagree, and honesty requires naming how.
//    uspCachePrincipal treats @TtlMinutes = NULL as "use the connection's
//    policy", but ICrawlStateStore.CachePrincipalAsync takes a TimeSpan rather
//    than a TimeSpan?, and SqlCrawlStateStore therefore always sends a number.
//    An operator who lowers PrincipalTtlMinutes on the connection has NOT
//    lowered it for this caller: the clamp in sql/33 applies to negative answers
//    only, so for a positive one the number sent from here wins. Settings:
//    PrincipalCacheTtlMinutes is how a deployment keeps the two in step until
//    the seam can express "unspecified"; crawl.vwPrincipalCacheTtl is where the
//    disagreement would be visible.
//
//    This class deliberately does NOT hold a second, shorter opinion for
//    negative answers even though sql/33 would honour it. A caller may be
//    stricter than the policy when it knows something the policy does not, and
//    this one knows nothing about a cluster group that the operator who set the
//    per-connection number does not know better. Hard-coding sixty minutes here
//    would silently overrule a deployment that had deliberately chosen four
//    hours, and would rebuild the convention sql/33 exists to have removed.
//
// 6. WITH NO STORE, NOTHING CHANGES - AND THAT IS CHECKED, NOT ASSUMED.
//
//    A connector with no Settings:StateConnectionString gets NullCrawlStateStore
//    whose IsEnabled is false, and this class reads that once and then never
//    calls the store at all. It does not call a no-op and interpret the empty
//    answer, because "the store knows nothing about this group" and "there is no
//    store" are different facts, and only one of them is worth a round trip. The
//    behaviour is the pre-existing one - resolve in memory, once per run - not a
//    degraded mode.
//
//    A store that is configured and then FAILS is treated the same way, and this
//    is the one place this class swallows an exception. A cache read or write
//    that throws costs directory lookups, never correctness: the authoritative
//    answers are the explicit map and Entra, and both are still consulted. Ending
//    a nine-hour crawl because a cache write timed out would trade a slower
//    correct run for no run at all. It is reported once, with the exception, and
//    the store is not used again for the rest of the run so the log cannot fill
//    with the same failure a thousand times.
//
// NOT THREAD SAFE, unchanged. The dictionary was never guarded and the engine
// reads a source on one thread; ACLs are built on the read path.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Acl;

using Connector.Security.Logging;
using Microsoft.Graph;
using PushCore;
using PushCore.State;
using Serilog;

/// <summary>Turns cluster group names into grants.</summary>
public sealed class PrincipalResolver
{
    /// <summary>
    /// How long a directory answer may be reused when the caller states no
    /// preference: 720 minutes, which is what crawl.Connection.
    /// PrincipalTtlMinutes defaults to in sql/33.
    ///
    /// The two numbers are the same on purpose. They are set in different places
    /// - one in a schema, one in a build - and a caller that shipped a different
    /// default would put the connector and the database into a disagreement that
    /// only crawl.vwPrincipalCacheTtl could see.
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(720);

    /// <summary>
    /// The principal family every key written from here belongs to.
    ///
    /// One of the four values docs/CRAWL-STATE-REFERENCE.md documents for
    /// PrincipalMap.SourceType, and the only one of them that describes what is
    /// actually being cached: a name that Entra knows as onPremisesSamAccountName
    /// - which is to say an AD group's sAMAccountName - because that is the only
    /// thing the directory lookup matches on. A cluster-local POSIX group that
    /// has no such counterpart is cached under the same family as the NEGATIVE
    /// answer it produced, which is correct: the recorded fact is "Entra has no
    /// AD group by this name".
    ///
    /// Splitting into PosixGroup and RangerGroup was considered and is not
    /// possible here even if it were desirable. HdfsAclBuilder unions the
    /// filesystem's groups and Ranger's before handing them over, so by this
    /// point a name has no origin; keying the same name under two families would
    /// store the same answer twice and expire the copies at different moments.
    /// </summary>
    private const string CachedSourceType = "AdGroup";

    private readonly Dictionary<string, string> explicitMap;
    private readonly GraphServiceClient? graph;
    private readonly ILogger log;
    private readonly ICrawlStateStore store;

    /// <summary>
    /// Whether this run writes nothing. A dry run reads the cache and never
    /// writes it: the row would be a real TTL'd entry indistinguishable from a
    /// real run's, and "writes nothing to Graph" is a weaker claim than the one
    /// a dry run makes.
    ///
    /// It does not change what is READ, deliberately. A dry run that resolved
    /// differently from a real one would stop being a rehearsal of it.
    /// </summary>
    private readonly bool isDryRun;
    private readonly TimeSpan? cacheTtl;
    private readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reportedMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Groups whose "resolves to nothing" came out of the persistent cache
    /// rather than out of a lookup this run made.
    ///
    /// Kept only so the warning can say so. "This group does not resolve" and
    /// "this group did not resolve within the last hour" send an operator to two
    /// different places, and the second one is the answer whenever a group has
    /// just been created - which is the failure mode persisting a negative
    /// answer introduces and the one thing it owes a person in return.
    /// </summary>
    private readonly HashSet<string> negativesFromCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the persistent cache is worth touching at all. False for a
    /// connector with no state store, false when directory lookups are off - see
    /// decision 1 in the file header - and false for the rest of the run once the
    /// store has thrown.
    /// </summary>
    private bool storeUsable;

    /// <summary>Initializes a new instance of the <see cref="PrincipalResolver"/> class.</summary>
    /// <param name="explicitMap">Cluster group name to Entra group object ID.</param>
    /// <param name="graph">A Graph client for directory lookups, or null when they are off.</param>
    /// <param name="log">Where to report unresolved groups.</param>
    /// <param name="store">
    /// The crawl state store holding crawl.PrincipalMap, or null when no
    /// ConnectorState database is configured. Null and NullCrawlStateStore are
    /// the same answer and both mean "resolve in memory, once per run", which is
    /// what this class did before the store existed.
    /// </param>
    /// <param name="cacheTtl">
    /// How long a directory answer may be reused, or null for
    /// <see cref="DefaultCacheTtl"/>. The database lowers this for a negative
    /// answer and never raises it for either.
    /// </param>
    public PrincipalResolver(
        IReadOnlyDictionary<string, string> explicitMap,
        GraphServiceClient? graph,
        ILogger log,
        ICrawlStateStore? store = null,
        TimeSpan? cacheTtl = null,
        bool isDryRun = false)
    {
        this.explicitMap = new Dictionary<string, string>(explicitMap, StringComparer.OrdinalIgnoreCase);
        this.graph = graph;
        this.log = log;
        this.store = store ?? NullCrawlStateStore.Instance;
        this.cacheTtl = cacheTtl;
        this.isDryRun = isDryRun;

        // Read once. IsEnabled is a property of the store's identity rather than
        // of its current health, so re-reading it per group would buy nothing and
        // would make the "stop using it after a failure" rule below unreachable.
        this.storeUsable = this.store.IsEnabled && graph is not null;
    }

    /// <summary>Gets the cluster groups that could not be resolved this run.</summary>
    /// <remarks>
    /// Includes groups whose negative answer was served from the persistent
    /// cache. A caller counting these is asking "which groups grant nothing on
    /// this run", and where the nothing came from does not change the answer.
    /// </remarks>
    public IReadOnlyCollection<string> Unresolved => this.reportedMisses;

    /// <summary>Parses the Settings:EntraGroupMap value.</summary>
    /// <param name="value">Semicolon-separated name=objectId pairs.</param>
    /// <returns>The map.</returns>
    public static Dictionary<string, string> ParseMap(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (string pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0 || equals == pair.Length - 1)
            {
                continue;
            }

            map[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
        }

        return map;
    }

    /// <summary>Resolves a set of cluster group names to grants, dropping the ones it cannot.</summary>
    /// <param name="groupNames">The cluster group names.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One grant per resolved group, deduplicated.</returns>
    /// <remarks>
    /// The names are collected before any of them is resolved, so the persistent
    /// cache can be asked about all of this call's unknowns in one round trip
    /// rather than one per name. That is the batching rule ICrawlStateStore's
    /// header states, and it is worth the extra pass: on the first file of a run
    /// it turns a directory's worth of groups into a single call, and on every
    /// file after that the in-memory cache answers and the list is empty.
    /// </remarks>
    public async Task<List<PushAclEntry>> ResolveAsync(
        IEnumerable<string> groupNames, CancellationToken cancellationToken)
    {
        var grants = new List<PushAclEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<string>();

        foreach (string name in groupNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            distinct.Add(name);
        }

        await this.WarmFromStoreAsync(distinct, cancellationToken);

        foreach (string name in distinct)
        {
            string? objectId = await this.ResolveOneAsync(name, cancellationToken);

            if (objectId is null)
            {
                continue;
            }

            grants.Add(new PushAclEntry(PushAclType.Group, objectId));
        }

        return grants;
    }

    /// <summary>Escapes a value for an OData string literal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The escaped value.</returns>
    private static string Escape(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Seeds the in-memory cache from crawl.PrincipalMap, in one call, for the
    /// names this run has not already answered.
    /// </summary>
    /// <param name="names">The distinct names this call is about to resolve.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// Names the explicit map answers are not asked about. They are answered
    /// without a lookup and are never written to the store, so a row for one
    /// could only be a leftover from a configuration that has since changed -
    /// and asking about it would be paying for the chance to be misled.
    ///
    /// An absent key is a miss, and a present key with a null object ID is a
    /// negative HIT. Conflating the two would turn every cached negative back
    /// into a directory lookup and undo the half of this feature that costs the
    /// most to get wrong.
    /// </remarks>
    private async Task WarmFromStoreAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (!this.storeUsable)
        {
            return;
        }

        List<string>? ask = null;

        foreach (string name in names)
        {
            if (this.cache.ContainsKey(name) || this.MappedExplicitly(name) is not null)
            {
                continue;
            }

            (ask ??= new List<string>()).Add(name);
        }

        if (ask is null)
        {
            return;
        }

        IReadOnlyDictionary<string, PrincipalGrant> answers;

        try
        {
            answers = await this.store.ResolvePrincipalsAsync(CachedSourceType, ask, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.StoreFailed(ex, "read");
            return;
        }

        foreach (KeyValuePair<string, PrincipalGrant> answer in answers)
        {
            // The store's key, not the one asked for. They differ only in case,
            // and only where the database's collation is case sensitive, but the
            // in-memory cache is case insensitive so either spelling hits.
            string? objectId = answer.Value.EntraObjectId?.ToString("D");

            this.cache[answer.Key] = objectId;

            if (objectId is null)
            {
                this.negativesFromCache.Add(answer.Key);
            }
        }
    }

    /// <summary>Resolves one name, consulting and populating both levels of cache.</summary>
    /// <param name="name">The cluster group name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The Entra object ID, or null when the group grants nothing.</returns>
    private async Task<string?> ResolveOneAsync(string name, CancellationToken cancellationToken)
    {
        if (this.cache.TryGetValue(name, out string? cached))
        {
            this.ReportIfUnresolved(name, cached);
            return cached;
        }

        // The configuration first, and before the store: see decision 1. An
        // operator's written statement about what a group means outranks a
        // recorded observation of what the directory said.
        string? resolved = this.MappedExplicitly(name);
        bool askedTheDirectory = false;

        if (resolved is null && this.graph is not null)
        {
            resolved = await this.LookUpAsync(name, cancellationToken);
            askedTheDirectory = true;
        }

        this.cache[name] = resolved;

        if (askedTheDirectory)
        {
            // Only a directory answer is written down, positive or negative. An
            // explicit-map answer is configuration and a null produced by having
            // no directory to ask is not an answer at all - persisting either
            // would put a row in crawl.PrincipalMap that reads as "Entra said so"
            // when Entra was never asked.
            await this.RememberAsync(name, resolved, cancellationToken);
        }

        this.ReportIfUnresolved(name, resolved);

        return resolved;
    }

    /// <summary>Reads the operator's own statement about what a group means.</summary>
    /// <param name="name">The cluster group name.</param>
    /// <returns>The mapped object ID in canonical form, or null when there is no usable mapping.</returns>
    /// <remarks>
    /// A mapping whose value is not a GUID returns null rather than being passed
    /// on, which sends the name to the directory as though it had not been
    /// mapped at all. That is the pre-existing behaviour and it is the safe one:
    /// a malformed object ID would be refused by Graph on every item that
    /// carried it.
    /// </remarks>
    private string? MappedExplicitly(string name)
    {
        return this.explicitMap.TryGetValue(name, out string? mapped) && Guid.TryParse(mapped, out Guid parsed)
            ? parsed.ToString("D")
            : null;
    }

    /// <summary>Writes one directory answer to crawl.PrincipalMap.</summary>
    /// <param name="name">The cluster group name, which is the cache key.</param>
    /// <param name="objectId">What the directory said, or null for "nothing".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    /// <remarks>
    /// The connection is not named here and cannot be. SqlCrawlStateStore takes
    /// it from the run it has open, so the row lands under the connection that
    /// is crawling and this class has no way to address another connection's
    /// mappings - which is what makes "two connections mapping the same name to
    /// different principals" a non-problem rather than a rule somebody has to
    /// remember. The primary key is (ConnectionId, SourceType, SourceKey).
    ///
    /// This writes on a dry run too, and that is worth stating rather than
    /// hiding: a dry run promises not to change the INDEX, and the row written
    /// here is a cache entry with a TTL whose content is identical to what a real
    /// run would have written. Nothing about the corpus, the checkpoints or the
    /// pending deletes is touched.
    /// </remarks>
    private async Task RememberAsync(string name, string? objectId, CancellationToken cancellationToken)
    {
        if (!this.storeUsable)
        {
            return;
        }

        Guid? id = null;

        if (objectId is not null)
        {
            if (!Guid.TryParse(objectId, out Guid parsed))
            {
                // PrincipalMap.EntraObjectId is UNIQUEIDENTIFIER, so a value
                // Graph returned that is not a GUID cannot be stored as one. It
                // is still returned as a grant - Graph gave it to us - it simply
                // is not remembered. Silent because it cannot happen against the
                // real directory and a warning per group per run would be noise
                // in the one place that is already noisy.
                return;
            }

            id = parsed;
        }

        // A DRY RUN READS THE CACHE AND NEVER WRITES IT. The row would be a real
        // TTL'd entry, indistinguishable from a real run's, and a run advertised
        // as writing nothing must not leave one behind - "writes nothing to
        // Graph" is a weaker claim than the one a dry run makes.
        //
        // Placed here rather than at the read, on purpose: a dry run that
        // resolved principals differently from a real run would stop being a
        // rehearsal of it, and the resolved ACL is what the preview's item
        // counts and skip decisions rest on.
        if (this.isDryRun)
        {
            return;
        }

        try
        {
            await this.store.CachePrincipalAsync(
                new PrincipalGrant(name, id, id is null ? null : "group"),
                CachedSourceType,
                this.cacheTtl,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.StoreFailed(ex, "written to");
        }
    }

    /// <summary>Reports a group that grants nothing, once per run.</summary>
    /// <param name="name">The cluster group name.</param>
    /// <param name="resolved">What it resolved to. Non-null means there is nothing to report.</param>
    /// <remarks>
    /// Once per group per run, not once per file. At a million files that is the
    /// difference between a line and a log nobody can read.
    ///
    /// This moved off the lookup and onto the ANSWER when the store was wired in,
    /// and the move is the point rather than a refactor: a negative served out of
    /// crawl.PrincipalMap never reaches a lookup, so a warning attached to the
    /// lookup would have gone quiet for exactly the groups whose absence had just
    /// been made to last for an hour. Unresolved would have emptied with it.
    /// </remarks>
    private void ReportIfUnresolved(string name, string? resolved)
    {
        if (resolved is not null || !this.reportedMisses.Add(name))
        {
            return;
        }

        if (this.negativesFromCache.Contains(name))
        {
            this.log.Warning(
                "Cluster group {GroupName} grants nothing, from the identity cache rather than from a lookup " +
                "made now: an earlier run asked the directory and was told there is no such group. That answer " +
                "expires within crawl.Connection.PrincipalNegativeTtlMinutes, 60 by default, so a group created " +
                "since is invisible until it does. To act sooner, map it in Settings:EntraGroupMap, which is " +
                "read before the cache.",
                name);

            return;
        }

        this.log.Warning(
            "Cluster group {GroupName} does not resolve to an Entra group, so it grants nothing. " +
            "Items readable only by it will be skipped. Add it to Settings:EntraGroupMap, or enable " +
            "Settings:ResolveGroupsFromDirectory if its name matches an AD group synchronised to Entra.",
            name);
    }

    /// <summary>Gives up on the identity cache for the rest of the run.</summary>
    /// <param name="ex">What the store threw.</param>
    /// <param name="operation">"read" or "written to", for the message.</param>
    /// <remarks>
    /// The only swallowed exception in this class, and the reasoning is in
    /// decision 6 of the file header: the cache is a memo of an authoritative
    /// answer, both authorities are still consulted, and the cost of a failure
    /// here is directory calls rather than wrong grants. Reported with the
    /// exception so the cause is in the log, and once, because the alternative on
    /// a dead database is the same stack trace a thousand times over.
    ///
    /// Wrapped rather than logged raw, and this is the one place in this class
    /// where that matters: what fails here is SqlClient, and a SqlException
    /// quotes the server it could not reach. Serilog renders an exception through
    /// ToString(), which no enricher can reach, so the wrap is the only thing
    /// standing between a connection string and a log sink. PushLoggingRedaction-
    /// Tests fails the build over an unwrapped one, and did over this line.
    /// </remarks>
    private void StoreFailed(Exception ex, string operation)
    {
        this.storeUsable = false;

        this.log.Warning(
            RedactedException.Wrap(ex),
            "The identity cache in crawl.PrincipalMap could not be {Operation} and will not be used again this " +
            "run. Group resolution continues against Settings:EntraGroupMap and the directory, which are the " +
            "authoritative answers - the cache only spares the lookups - so the grants on this run are correct " +
            "and every group will be looked up again.",
            operation);
    }

    /// <summary>Asks Entra which group carries this on-premises name.</summary>
    /// <param name="name">The cluster group name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The object ID of the single match, or null.</returns>
    private async Task<string?> LookUpAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var page = await this.graph!.Groups.GetAsync(
                request =>
                {
                    // The on-premises name is the one Hadoop knows. Matching on
                    // displayName instead would match a different group that
                    // merely reads the same, which is the kind of near-miss that
                    // grants the wrong people access.
                    request.QueryParameters.Filter = $"onPremisesSamAccountName eq '{Escape(name)}'";
                    request.QueryParameters.Select = ["id", "displayName"];
                    request.QueryParameters.Top = 2;
                },
                cancellationToken);

            List<Microsoft.Graph.Models.Group> matches = page?.Value ?? [];

            if (matches.Count == 1)
            {
                return matches[0].Id;
            }

            if (matches.Count > 1)
            {
                // Two groups claiming the same on-premises name is a directory
                // problem, and picking one would be picking an audience.
                //
                // Cached as a negative like any other, which is the right
                // reading: the directory was asked and gave an answer this
                // connector cannot act on, and asking again inside the hour would
                // get the same one. The warning is unconditional rather than
                // once-per-run because a second directory whose names collide is
                // worth seeing every time it is looked at - and after the first
                // file of a run it is not looked at again.
                this.log.Warning(
                    "Cluster group {GroupName} matches more than one Entra group by onPremisesSamAccountName. " +
                    "It grants nothing until Settings:EntraGroupMap says which one is meant.",
                    name);
            }

            return null;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            throw new PushSourceAuthenticationException(
                "Graph refused a group lookup. Settings:ResolveGroupsFromDirectory needs the " +
                "GroupMember.Read.All application permission, which the rest of this connector does not use - " +
                "grant it deliberately, or map the groups in Settings:EntraGroupMap instead.",
                ex);
        }
    }
}
