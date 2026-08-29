// ---------------------------------------------------------------------------
// Format.cs
// How a number, a timestamp and a duration are rendered, decided once.
//
// This is a monitoring dashboard, so the formatting rules are not cosmetic.
//
//   EVERY TIMESTAMP IS UTC AND SAYS SO. The database stores UTC throughout -
//   DATETIME2(3), SYSUTCDATETIME(), no offsets anywhere - and the connector runs
//   on servers whose local time an operator may not know. Rendering in the
//   browser's zone would mean a run in a ticket and the same run on this page
//   showing different times, which is how a wrong conclusion gets written down.
//   The pages label the columns UTC rather than converting.
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

    /// <summary>Formats a UTC timestamp. The pages label the column UTC; this does not convert.</summary>
    /// <param name="value">The timestamp, or null.</param>
    /// <returns>The formatted timestamp, or an em dash.</returns>
    public static string Timestamp(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : None;
    }

    /// <summary>Formats a UTC timestamp to the minute, for dense tables.</summary>
    /// <param name="value">The timestamp, or null.</param>
    /// <returns>The formatted timestamp, or an em dash.</returns>
    public static string TimestampShort(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : None;
    }

    /// <summary>Formats a date.</summary>
    /// <param name="value">The date.</param>
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
