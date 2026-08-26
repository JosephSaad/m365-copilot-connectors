// ---------------------------------------------------------------------------
// ContentTruncator.cs
// Items are capped at 4 MB by the platform. Emitting an oversize item wastes a
// round trip and gets rejected by the agent with a message that names neither
// the item nor the size, so the cap is enforced here instead.
// ---------------------------------------------------------------------------

namespace Connector.Security.Content
{
    using System;
    using System.Globalization;
    using System.Text;

    /// <summary>The outcome of a truncation check.</summary>
    public readonly struct TruncationResult
    {
        /// <summary>Initializes a result.</summary>
        public TruncationResult(string content, int originalBytes, int finalBytes, bool truncated)
        {
            this.Content = content;
            this.OriginalBytes = originalBytes;
            this.FinalBytes = finalBytes;
            this.Truncated = truncated;
        }

        /// <summary>Gets the content to emit.</summary>
        public string Content { get; }

        /// <summary>Gets the size of the content as read from the data source.</summary>
        public int OriginalBytes { get; }

        /// <summary>Gets the size of the content being emitted.</summary>
        public int FinalBytes { get; }

        /// <summary>Gets a value indicating whether anything was removed.</summary>
        public bool Truncated { get; }
    }

    /// <summary>Truncates item content on a UTF-8 boundary.</summary>
    public static class ContentTruncator
    {
        private const string MarkerFormat =
            "\n\n[Content truncated by the connector: {0} of {1} bytes indexed.]";

        /// <summary>
        /// Returns content that encodes to at most <paramref name="maxBytes"/> UTF-8
        /// bytes. Truncation is visible in the indexed text rather than silent, so a
        /// user reading a search result knows the body is incomplete.
        /// </summary>
        public static TruncationResult Truncate(string content, int maxBytes)
        {
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "The content cap must be positive.");
            }

            string text = content ?? string.Empty;
            int originalBytes = Encoding.UTF8.GetByteCount(text);

            if (originalBytes <= maxBytes)
            {
                return new TruncationResult(text, originalBytes, originalBytes, false);
            }

            string marker = string.Format(
                CultureInfo.InvariantCulture,
                MarkerFormat,
                maxBytes,
                originalBytes);

            int markerBytes = Encoding.UTF8.GetByteCount(marker);
            int budget = Math.Max(0, maxBytes - markerBytes);

            byte[] encoded = Encoding.UTF8.GetBytes(text);
            int cut = Math.Min(budget, encoded.Length);

            // Never split a multi byte sequence: back off over continuation bytes.
            while (cut > 0 && (encoded[cut] & 0xC0) == 0x80)
            {
                cut--;
            }

            string truncated = Encoding.UTF8.GetString(encoded, 0, cut) + marker;
            int finalBytes = Encoding.UTF8.GetByteCount(truncated);

            return new TruncationResult(truncated, originalBytes, finalBytes, true);
        }
    }
}
