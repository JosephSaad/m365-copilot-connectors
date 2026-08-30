// ---------------------------------------------------------------------------
// CdpCrawlState.cs
// How a CDP connector gets hold of the crawl state store the host built.
//
// WHY THIS FILE EXISTS AT ALL. PushHost builds the store from the factory
// Program.cs hands it, keeps it for the length of the run, disposes it at the
// end, and gives it to PushEngine. IPushConnector.CreateSource is handed a
// PushSourceContext, and a PushSourceContext carries the options, the credential,
// the secret provider, the logger and the resume marker - not the store. So a
// connector that wants to give something to its source cannot reach it.
//
// The identity cache needs exactly that. PrincipalResolver caches a directory
// lookup in crawl.PrincipalMap, and the store holding that table is the one the
// host already opened - not a second one. A second store would be a second SQL
// connection, and worse, one with no run open: SqlCrawlStateStore takes the
// connection ID from the run it is recording against and refuses every call made
// before BeginRunAsync, so the resolver's first lookup would throw rather than
// cache.
//
// This is therefore a HANDOFF rather than a service locator, and the difference
// is the size of what passes through it: one object, published by the factory
// this executable already supplies, read by the three connectors in this
// executable, and nothing else in the process can be reached through it.
//
// THE ORDER IS WHAT MAKES IT SAFE, and it is worth stating because nothing in
// the type system enforces it. PushHost invokes the factory before it builds the
// engine; PushEngine opens the run before it calls CreateSource - OpenRunAsync
// is the line above `this.connector.CreateSource(context)` and its own comment
// says why. So by the time a connector reads Current the store exists, and by
// the time the resolver calls it a run is open. A future reordering that created
// the source before the run would not corrupt anything: SqlCrawlStateStore
// throws InvalidOperationException naming BeginRunAsync, which is a loud failure
// rather than a silent miscache.
//
// THE ALTERNATIVES, and why not:
//
//   Add the store to PushSourceContext. This is the right long-term answer and
//   it is a change to PushCore, which every connector family shares. It belongs
//   in a change that is about that seam rather than riding in underneath a
//   connector's cache.
//
//   Build a second store in CreateSource from Settings:StateConnectionString.
//   Two connections, two IAsyncDisposables, and the no-open-run problem above.
//   It also re-reads a setting CrawlStateWiring exists to keep in one place.
//
//   Have PrincipalResolver read a holder itself. That would make
//   CdpConnector.Source depend on this executable, which is upside down, and
//   would leave the resolver untestable except through a static.
//
// LIFETIME. Current is meaningful only for the run the factory was called for.
// PushHost disposes the store when that run ends, and this executable runs one
// connector and exits, so there is no second run to hand a disposed store to.
// Nothing here keeps the store alive: the field is a reference to an object
// PushHost owns.
//
// The second thing the identity cache needs from the host is its TTL, which is
// here for the same reason CrawlStateWiring holds the connection string's key
// name: three connectors each reading a setting for themselves is three chances
// to spell it differently, and a mistyped key here is not an error - it is a
// cache that silently keeps a different policy from the one somebody configured.
// ---------------------------------------------------------------------------

namespace CdpGraphPush;

using CdpConnector.Source.Acl;
using PushCore;
using PushCore.State;
using Serilog;

/// <summary>Publishes the run's crawl state store to the connectors in this executable.</summary>
public static class CdpCrawlState
{
    /// <summary>The Settings key holding how long a resolved principal may be reused.</summary>
    /// <remarks>
    /// Absent is the normal case and means <see cref="PrincipalResolver.DefaultCacheTtl"/>,
    /// which is the same 720 minutes crawl.Connection.PrincipalTtlMinutes defaults
    /// to - so a deployment that has never heard of this key has a connector and a
    /// database that agree. It exists for the deployment that lowers the column:
    /// uspCachePrincipal only clamps a NEGATIVE answer, so for a positive one the
    /// number this caller sends wins, and without this key the only way to send a
    /// different one would be a rebuild.
    /// </remarks>
    public const string PrincipalCacheTtlSetting = "PrincipalCacheTtlMinutes";

    /// <summary>
    /// The store, or the null one until a run publishes a real one.
    ///
    /// Never null, so a connector reading it does not have to decide what null
    /// means. NullCrawlStateStore.IsEnabled is false and every caller in this
    /// executable branches on that rather than on a reference test.
    /// </summary>
    private static ICrawlStateStore current = NullCrawlStateStore.Instance;

    /// <summary>Gets the store this run is using. Never null; may be disabled.</summary>
    public static ICrawlStateStore Current => Volatile.Read(ref current);

    /// <summary>
    /// Builds the store exactly as every other push executable does, and
    /// publishes it for this executable's connectors on the way past.
    /// </summary>
    /// <param name="options">Validated configuration.</param>
    /// <param name="log">Where to report progress.</param>
    /// <returns>The store, or null to run without durable crawl memory.</returns>
    /// <remarks>
    /// The return value is PushHost's and is unchanged - null still means "no
    /// crawl state", and the host still logs and behaves exactly as it does for
    /// SqlGraphPush and SqlHierarchyPush. What this adds is the assignment, and
    /// the null case assigns too: leaving a previous value in place would let a
    /// connector configured WITHOUT a state store read one that a different
    /// configuration had published, which is the one way a handoff like this can
    /// be wrong in a way nobody notices.
    /// </remarks>
    public static ICrawlStateStore? FromSettings(PushOptions options, ILogger log)
    {
        ICrawlStateStore? store = CrawlStateWiring.FromSettings(options, log);

        Volatile.Write(ref current, store ?? NullCrawlStateStore.Instance);

        return store;
    }

    /// <summary>Reads how long a resolved principal may be reused.</summary>
    /// <param name="options">Validated configuration.</param>
    /// <returns>The configured lifetime, or the resolver's default.</returns>
    /// <remarks>
    /// Zero, negative and unparseable all take the default rather than being
    /// passed on. A TTL of zero is not a short cache: SqlCrawlStateStore rounds
    /// it up to one minute and the row is written already all but expired, which
    /// is a cache that never hits and never says that it never hits. sql/23 makes
    /// the same correction at the other end for the same reason.
    /// </remarks>
    public static TimeSpan? PrincipalCacheTtl(PushOptions options)
    {
        int minutes = options.Setting(PrincipalCacheTtlSetting, 0);

        // NULL WHEN UNSET, and that is the whole point of the nullable seam.
        // uspCachePrincipal reads crawl.Connection.PrincipalTtlMinutes when the
        // caller states nothing, so an operator who lowers that column has
        // actually lowered it. Sending a number here unconditionally - which is
        // what this did while the store's parameter was a non-nullable TimeSpan
        // - made the column govern nothing for positive answers, leaving sql/33's
        // clamp, which only touches negatives, as the only thing the database
        // controlled. A policy column no caller can defer to is a setting that
        // does nothing.
        //
        // The setting remains as an override for a deployment that wants a
        // different number from the one in the database, which is a fair thing
        // to want and is now distinguishable from having no opinion.
        return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }
}
