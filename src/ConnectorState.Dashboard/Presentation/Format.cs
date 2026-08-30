// ---------------------------------------------------------------------------
// Format.cs
// How a number, a timestamp and a duration are rendered, decided once.
//
// This is a monitoring dashboard, so the formatting rules are not cosmetic.
//
//   EVERY TIMESTAMP SAYS WHICH ZONE IT IS IN, WHICH IS WHY IT CAN BE CONVERTED
//   AT ALL. The database stores UTC throughout - DATETIME2(3),
//   SYSUTCDATETIME(), no offsets anywhere - and the connector runs on servers
//   whose local time an operator may not know.
//
//   This file used to say that rendering in the browser's zone would mean a run
//   in a ticket and the same run on this page showing different times, which is
//   how a wrong conclusion gets written down. That is true of an UNLABELLED
//   local time and it is the reason the zone is now named in the masthead, in
//   the colophon and in every column header that carries a timestamp, and the
//   reason UTC is one click away and stays one click away with JavaScript
//   disabled. The formatters below therefore REQUIRE a DisplayZone: there is no
//   overload that renders a timestamp without one, so a page added later cannot
//   print a bare time that a reader has to guess the meaning of. That is the
//   whole enforcement, and it is a compile error rather than a review comment.
//
//   Date() takes no zone, and that is deliberate rather than an omission - see
//   the header of DisplayZone.cs. It renders the UTC dates vwDailyActivity
//   grouped by, and shifting a bucket's label into a viewer's zone would put a
//   bar dated the 14th over a day the database computed for the 15th.
//
//   NULL IS NOT ZERO. A null UnchangedPercent means the run touched nothing;
//   showing 0% would say the change detection failed. Every formatter here takes
//   the nullable and renders an em dash for null, so a page cannot accidentally
//   coalesce one into a plausible figure.
//
//   INVARIANT CULTURE, ALWAYS. A page whose thousands separator depends on the
//   operator's browser is a page where two people reading the same incident
//   quote different numbers.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

using System.Globalization;

/// <summary>Renders figures for display. No formatting decision lives outside this class.</summary>
public static class Format
{
    /// <summary>What a null renders as. An em dash, meaning "there is no value", not "zero".</summary>
    public const string None = "—";

    /// <summary>Formats a count with thousands separators.</summary>
    /// <param name="value">The count.</param>
    /// <returns>The formatted count.</returns>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Formats a count that may be absent.</summary>
    /// <param name="value">The count, or null.</param>
    /// <returns>The formatted count, or an em dash.</returns>
    public static string Count(long? value) => value.HasValue ? Count(value.Value) : None;

    /// <summary>Formats a count compactly for an axis label: 18.0k, 1.4M.</summary>
    /// <param name="value">The count.</param>
    /// <returns>The abbreviated count.</returns>
    public static string Compact(long value)
    {
        if (value >= 1_000_000_000)
        {
            return (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B";
        }

        if (value >= 1_000_000)
        {
            return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= 1_000)
        {
            return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a percentage the database already computed to one decimal place.</summary>
    /// <param name="value">The percentage, or null when the denominator was zero.</param>
    /// <returns>The formatted percentage, or an em dash.</returns>
    public static string Percent(decimal? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : None;
    }

    /// <summary>Formats a decimal to one place.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>The formatted value, or an em dash.</returns>
    public static string Decimal1(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("N1", CultureInfo.InvariantCulture) : None;
    }

    /// <summary>Formats a decimal to two places.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>The formatted value, or an em dash.</returns>
    public static string Decimal2(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("N2", CultureInfo.InvariantCulture) : None;
    }

    /// <summary>Formats a byte count in binary units.</summary>
    /// <param name="bytes">The count of bytes.</param>
    /// <returns>The formatted size.</returns>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        string[] units = { "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = bytes;
        int unit = -1;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Formats a UTC timestamp in the page's display zone.</summary>
    /// <param name="value">The timestamp as the store holds it, in UTC, or null.</param>
    /// <param name="zone">The zone this page is rendering in. Required, on purpose.</param>
    /// <returns>The formatted timestamp, or an em dash.</returns>
    /// <remarks>
    /// The zone is a parameter rather than ambient state, and there is no
    /// one-argument overload. Both halves matter: ambient state would make the
    /// same call render differently depending on what ran before it, and an
    /// overload would let a new page print an unlabelled time by writing the
    /// shorter thing.
    /// </remarks>
    public static string Timestamp(DateTime? value, DisplayZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return value.HasValue
            ? zone.ConvertFromUtc(value.Value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : None;
    }

    /// <summary>Formats a UTC timestamp to the minute, in the page's display zone.</summary>
    /// <param name="value">The timestamp as the store holds it, in UTC, or null.</param>
    /// <param name="zone">The zone this page is rendering in.</param>
    /// <returns>The formatted timestamp, or an em dash.</returns>
    public static string TimestampShort(DateTime? value, DisplayZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return value.HasValue
            ? zone.ConvertFromUtc(value.Value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : None;
    }

    /// <summary>Formats a UTC date. Takes no zone; see the file header.</summary>
    /// <param name="value">The date, as the database grouped it.</param>
    /// <returns>The formatted date.</returns>
    public static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Formats an elapsed time in seconds as hours, minutes and seconds.</summary>
    /// <param name="seconds">The elapsed seconds.</param>
    /// <returns>The formatted duration.</returns>
    public static string Duration(int seconds)
    {
        if (seconds < 0)
        {
            // A clock skew between the connector host and the SQL host, which is
            // worth showing rather than clamping to zero.
            return None;
        }

        if (seconds < 60)
        {
            return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalHours < 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{span.Minutes}m {span.Seconds:00}s");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours}h {span.Minutes:00}m");
    }

    /// <summary>Formats an elapsed time in minutes as a coarse age.</summary>
    /// <param name="minutes">The elapsed minutes, or null when there is no reference point.</param>
    /// <returns>The formatted age, or an em dash.</returns>
    public static string Age(int? minutes)
    {
        if (!minutes.HasValue)
        {
            return None;
        }

        int value = minutes.Value;

        if (value < 0)
        {
            return None;
        }

        if (value < 60)
        {
            return value.ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (value < 60 * 48)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value / 60}h {value % 60:00}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value / (60 * 24)}d");
    }

    /// <summary>Formats a millisecond figure from the timing tables.</summary>
    /// <param name="milliseconds">The value.</param>
    /// <returns>The formatted value.</returns>
    public static string Milliseconds(decimal milliseconds)
    {
        if (milliseconds >= 10_000)
        {
            return (milliseconds / 1000m).ToString("N1", CultureInfo.InvariantCulture) + " s";
        }

        return milliseconds.ToString(milliseconds >= 100 ? "N0" : "N1", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>Shortens a hash for display, keeping enough to compare two by eye.</summary>
    /// <param name="hex">The full hex hash.</param>
    /// <returns>The first twelve characters, or the whole value if it is shorter.</returns>
    public static string HashPrefix(string hex)
    {
        return string.IsNullOrEmpty(hex) ? None : (hex.Length <= 12 ? hex : hex[..12]);
    }
}
