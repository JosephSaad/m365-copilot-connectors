// ---------------------------------------------------------------------------
// ITicketSource.cs
// The seam between the crawl protocol and the database. The redaction and
// watermark tests drive the real crawl code through a fake implementation of
// this interface.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SqlTicketsConnector.Logging;

    /// <summary>Which rows a read should return.</summary>
    public enum TicketReadMode
    {
        /// <summary>Every live row. Soft deleted rows are excluded; the agent removes what it no longer sees.</summary>
        FullCrawl = 0,

        /// <summary>Rows changed since the watermark, including soft deleted ones so deletes can be reported.</summary>
        Incremental = 1,
    }

    /// <summary>Reads ticket rows.</summary>
    public interface ITicketSource : IDisposable
    {
        /// <summary>Proves the data source is reachable and readable. Used by the connection wizard.</summary>
        Task ValidateAsync(CancellationToken ct);

        /// <summary>Streams rows after the watermark, ordered by (LastModified, TicketId).</summary>
        IAsyncEnumerable<TicketRow> ReadAsync(Watermark from, TicketReadMode mode, CancellationToken ct);
    }

    /// <summary>Creates a source for one operation.</summary>
    public interface ITicketSourceFactory
    {
        /// <summary>Creates a source. The metrics instance may be null for non-crawl operations.</summary>
        ITicketSource Create(CrawlMetrics metrics);

        /// <summary>Gets a log-safe description of the target, for example "sql01/Ops (WindowsIntegrated)".</summary>
        string Description { get; }
    }
}
