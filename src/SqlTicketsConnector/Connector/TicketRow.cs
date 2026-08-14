// ---------------------------------------------------------------------------
// TicketRow.cs
// One row of dbo.Tickets, decoupled from SqlDataReader so the crawl logic can
// be tested against a fake source with no SQL instance in sight.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;

    /// <summary>A single ticket. Field values are customer data and are never logged.</summary>
    public sealed class TicketRow
    {
        /// <summary>Gets or sets the primary key.</summary>
        public int TicketId { get; set; }

        /// <summary>Gets or sets the ticket title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the workflow status.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Gets or sets the assignee.</summary>
        public string AssignedTo { get; set; } = string.Empty;

        /// <summary>Gets or sets the ticket body, which becomes the indexed content.</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>Gets or sets the change watermark column, in UTC.</summary>
        public DateTime LastModifiedUtc { get; set; }

        /// <summary>Gets or sets a value indicating whether the row is soft deleted.</summary>
        public bool IsDeleted { get; set; }

        /// <summary>Gets this row's position in the crawl ordering.</summary>
        public Watermark Watermark
        {
            get { return new Watermark(this.LastModifiedUtc, this.TicketId); }
        }

        /// <summary>Gets the item ID used in the index: alphanumeric, 128 characters or fewer.</summary>
        public string ItemId
        {
            get { return "ticket" + this.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        }
    }
}
