// ---------------------------------------------------------------------------
// Watermark.cs
// The incremental crawl checkpoint.
//
// A timestamp alone is not enough. Several tickets can share one LastModified
// value, and a batch boundary can fall in the middle of that group: a strictly
// greater comparison then skips the rest of the group forever, and a greater or
// equal comparison re-emits the ones already sent. The checkpoint therefore
// carries (LastModified, TicketId) and the WHERE clause compares the pair.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Globalization;

    /// <summary>A position in the (LastModified, TicketId) ordering of dbo.Tickets.</summary>
    public readonly struct Watermark : IEquatable<Watermark>
    {
        /// <summary>Prefix identifying the composite marker format.</summary>
        public const string MarkerPrefix = "v2|";

        private readonly DateTime lastModifiedUtc;
        private readonly int ticketId;

        /// <summary>Initializes a watermark.</summary>
        public Watermark(DateTime lastModifiedUtc, int ticketId)
        {
            this.lastModifiedUtc = lastModifiedUtc.Kind == DateTimeKind.Utc
                ? lastModifiedUtc
                : DateTime.SpecifyKind(lastModifiedUtc, DateTimeKind.Utc);

            this.ticketId = ticketId;
        }

        /// <summary>Gets a watermark before every possible row.</summary>
        public static Watermark Beginning
        {
            get { return new Watermark(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), int.MinValue); }
        }

        /// <summary>Gets the last modified instant, in UTC.</summary>
        public DateTime LastModifiedUtc
        {
            get { return this.lastModifiedUtc; }
        }

        /// <summary>Gets the ticket ID that breaks ties within one instant.</summary>
        public int TicketId
        {
            get { return this.ticketId; }
        }

        /// <summary>
        /// True when the given row sorts after this watermark. This is the exact
        /// predicate the SQL WHERE clause implements; the two are kept in step by
        /// the composite watermark test.
        /// </summary>
        public bool IsAfter(DateTime rowLastModifiedUtc, int rowTicketId)
        {
            DateTime normalized = rowLastModifiedUtc.Kind == DateTimeKind.Utc
                ? rowLastModifiedUtc
                : DateTime.SpecifyKind(rowLastModifiedUtc, DateTimeKind.Utc);

            if (normalized > this.lastModifiedUtc)
            {
                return true;
            }

            return normalized == this.lastModifiedUtc && rowTicketId > this.ticketId;
        }

        /// <summary>True when the row sorts after this watermark.</summary>
        public bool IsAfter(TicketRow row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            return this.IsAfter(row.LastModifiedUtc, row.TicketId);
        }

        /// <summary>Renders the watermark for CrawlCheckpoint.CustomMarkerData.</summary>
        public string ToMarker()
        {
            return MarkerPrefix +
                   this.lastModifiedUtc.ToString("o", CultureInfo.InvariantCulture) +
                   "|" +
                   this.ticketId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a marker written by this connector.
        /// Accepts the composite v2 form, and a bare timestamp written by the
        /// previous version so an in-place upgrade does not force a full recrawl.
        /// An item ID marker, which older builds wrote, cannot be resumed from and
        /// returns false.
        /// </summary>
        public static bool TryParse(string marker, out Watermark watermark)
        {
            watermark = Beginning;

            if (string.IsNullOrWhiteSpace(marker))
            {
                return false;
            }

            string trimmed = marker.Trim();

            if (trimmed.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                string[] parts = trimmed.Substring(MarkerPrefix.Length).Split('|');
                DateTime parsedTime;
                int parsedId;

                if (parts.Length == 2 &&
                    DateTime.TryParse(
                        parts[0],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out parsedTime) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId))
                {
                    watermark = new Watermark(ToUtc(parsedTime), parsedId);
                    return true;
                }

                return false;
            }

            DateTime legacy;
            if (DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out legacy))
            {
                // Timestamp only marker from the previous build. Resuming from the
                // instant with the lowest possible ID re-reads that whole instant,
                // which is safe: re-emitting an item is cheap, losing one is not.
                watermark = new Watermark(ToUtc(legacy), int.MinValue);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Normalizes a parsed instant to UTC. RoundtripKind cannot be combined
        /// with AdjustToUniversal, so the conversion happens here: markers written
        /// by this connector always carry Z, and anything unspecified is treated as
        /// UTC because that is what the column holds.
        /// </summary>
        private static DateTime ToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    return value;

                case DateTimeKind.Local:
                    return value.ToUniversalTime();

                default:
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }

        /// <inheritdoc />
        public bool Equals(Watermark other)
        {
            return this.lastModifiedUtc == other.lastModifiedUtc && this.ticketId == other.ticketId;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is Watermark && this.Equals((Watermark)obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return this.lastModifiedUtc.GetHashCode() ^ this.ticketId.GetHashCode();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return this.ToMarker();
        }
    }
}
