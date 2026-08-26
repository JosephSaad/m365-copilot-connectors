// ---------------------------------------------------------------------------
// IHiveRowReader.cs
// A seam over ODBC, so the rest of the Hive path can be tested.
//
// OdbcDataReader is sealed and OdbcException cannot be constructed, which is
// the same wall the SQL family hit with SqlDataReader - and the reason its row
// mapping is only exercised against a real database. Putting one interface
// between the query and the mapping means the Hive mapping, the watermark
// ordering and the ACL decisions can all be tested with canned rows, and only
// the twenty lines that actually talk to the driver need a cluster.
//
// The row is a dictionary rather than a positional reader on purpose: a Hive
// query returns columns named "db.table.column" or bare depending on the
// driver's version and the transport, and normalising that once at the edge is
// better than every mapping coping with both.
//
// Text and Marker both render a value as a string and are deliberately NOT the
// same rendering, because they are read by two different parsers. Text feeds
// Graph, which wants ISO-8601. Marker feeds a literal in the next run's HiveQL,
// and HiveQL's timestamp grammar accepts neither the 'T' separator nor a
// trailing 'Z' - a marker in ISO-8601 casts to NULL inside the comparison, so
// the comparison matches nothing and every incremental run reads zero rows and
// reports success. One method each, so neither format can drift into the other.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hive;

/// <summary>One row, by column name.</summary>
public sealed class HiveRow
{
    // Hive's own timestamp literal grammar: a space separator, no zone suffix,
    // and up to nine fractional digits. Seven is all a .NET DateTime carries,
    // and Hive parses a shorter fraction than it allows.
    private const string HiveTimestampFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private readonly Dictionary<string, object?> values;

    /// <summary>Initializes a new instance of the <see cref="HiveRow"/> class.</summary>
    /// <param name="values">The column values, keyed by name.</param>
    public HiveRow(IDictionary<string, object?> values)
    {
        this.values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the column names, in the order the query returned them.</summary>
    public IEnumerable<string> Columns => this.values.Keys;

    /// <summary>Reads a column as text, or empty when it is null or absent.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The value.</returns>
    public string Text(string column)
    {
        object? value = this.Raw(column);

        return value switch
        {
            null => string.Empty,
            DateTime time => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("o"),
            DateTimeOffset offset => offset.UtcDateTime.ToString("o"),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    /// <summary>
    /// Reads a column as a marker that HiveQL can compare against.
    ///
    /// A DateTime or a DateTimeOffset becomes a UTC Hive timestamp literal;
    /// anything else - a bigint key, a string column, a driver that already
    /// hands back the timestamp as text - is whatever Text makes of it, because
    /// those need no reformatting to be comparable and reformatting them would
    /// be inventing a value the column does not hold.
    ///
    /// The rendering is culture-invariant explicitly: ':' in a custom format
    /// string is the culture's time separator rather than a colon, so a host in
    /// a culture that separates time with a dot would otherwise write a marker
    /// Hive cannot parse - which is this method's whole reason for existing.
    /// </summary>
    /// <param name="column">The column name.</param>
    /// <returns>The marker, or empty when the column is null or absent.</returns>
    public string Marker(string column)
    {
        object? value = this.Raw(column);

        return value switch
        {
            null => string.Empty,

            // Stamped as UTC rather than converted, for the reason Timestamp
            // gives: the column is documented as UTC and converting a value
            // that is already UTC drifts it at every daylight-saving change.
            DateTime time => DateTime.SpecifyKind(time, DateTimeKind.Utc)
                .ToString(HiveTimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.UtcDateTime
                .ToString(HiveTimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
            _ => this.Text(column),
        };
    }

    /// <summary>Reads a column as a timestamp, or null when it is not one.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The value.</returns>
    public DateTimeOffset? Timestamp(string column)
    {
        object? value = this.Raw(column);

        return value switch
        {
            null => null,

            // Hive hands back a DateTime with Kind Unspecified. The column is
            // documented as UTC, so it is stamped rather than converted -
            // converting a value that is already UTC is how a timestamp drifts
            // by the offset at every daylight-saving change.
            DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
            DateTimeOffset offset => offset,
            string text when DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>Reads a column as a number, or null.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The value.</returns>
    public double? Number(string column)
    {
        object? value = this.Raw(column);

        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Reads the raw value, treating DBNull as absent.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The value, or null.</returns>
    public object? Raw(string column)
    {
        if (!this.values.TryGetValue(column, out object? value))
        {
            // A bare name after the driver returned it qualified, or the other
            // way round. Matching on the last segment covers both without the
            // caller knowing which driver it is talking to.
            KeyValuePair<string, object?> match = this.values
                .FirstOrDefault(pair => pair.Key.EndsWith("." + column, StringComparison.OrdinalIgnoreCase));

            value = match.Key is null ? null : match.Value;
        }

        return value is DBNull ? null : value;
    }
}

/// <summary>Runs one query and returns its rows.</summary>
public interface IHiveRowReader : IAsyncDisposable
{
    /// <summary>Executes the query and streams the rows.</summary>
    /// <param name="query">The HiveQL to run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The rows.</returns>
    IAsyncEnumerable<HiveRow> QueryAsync(string query, CancellationToken cancellationToken);
}
