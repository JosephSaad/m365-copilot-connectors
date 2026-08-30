// ---------------------------------------------------------------------------
// CrawlRunLockedException.cs
// Another process is already crawling this connection.
//
// This is NOT a failure, and the type exists to keep it from being treated as
// one. A scheduled task that fires while the previous run is still going, or the
// passive half of an active/passive pair reaching its scheduled time, is the
// system working exactly as designed: sql/43's heartbeat lease refuses the
// second run so that two delete sweeps cannot diff the corpus against each
// other. The correct response is to do nothing and try again next interval.
//
// It is distinguished from every other store failure because the audiences
// differ. A store that cannot be reached, a credential that is rejected, an item
// Graph refuses - all of those want somebody's attention. This wants nobody's.
// Folding it into an ordinary failure would page an operator nightly for a job
// that is behaving correctly, and an alert that fires on correct behaviour is an
// alert that gets muted, which costs the alerts around it too.
//
// PushHost turns it into exit code 5. See the exit-code table in PushHost for
// why that is a new code rather than a reuse, and why the reasoning differs from
// the deliberate decision NOT to add one for refused items.
// ---------------------------------------------------------------------------

namespace PushCore.State;

using System;

/// <summary>Raised when another live run holds this connection's lease.</summary>
public sealed class CrawlRunLockedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CrawlRunLockedException"/> class.</summary>
    /// <param name="message">The server's message, which names the holding run, host and process.</param>
    /// <param name="inner">The underlying SQL error, kept for diagnostics.</param>
    public CrawlRunLockedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
