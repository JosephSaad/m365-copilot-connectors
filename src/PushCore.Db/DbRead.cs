// ---------------------------------------------------------------------------
// DbRead.cs
// Column readers shared by every connector on this path.
//
// This is SqlRead widened to DbDataReader, with one deliberate behavioural
// difference: the numeric readers CONVERT rather than cast.
//
// SqlRead.Integer can call GetInt32 because SQL Server's INT arrives as an
// Int32. Oracle's NUMBER does not - the managed provider surfaces it as a
// decimal whatever its scale, so GetInt32 throws InvalidCastException on a
// column that is an integer in every sense the schema cares about. Teradata's
// NUMBER and BYTEINT have the same property. Converting costs a boxed value per
// cell and removes an entire class of "works on SQL Server, throws on Oracle"
// defect that would otherwise surface one row into a live crawl, as exit 4,
// with the ordinal-only message that deliberately does not say what the value
// was.
// ---------------------------------------------------------------------------

namespace PushCore.Db;

using System.Data.Common;
using System.Globalization;

/// <summary>Column readers shared by every connector on the provider-agnostic path.</summary>
public static class DbRead
{
    /// <summary>Reads a string column, or empty when null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or an empty string.</returns>
    public static string Text(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);

        // GetValue then ToString rather than GetString: Oracle returns CLOB and
        // NVARCHAR2 through different CLR types, and a connector reading a
        // description column should not have to know which it got.
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Reads a datetime column as a round-trip UTC string.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value in "o" format.</returns>
    public static string Utc(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);
        return DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
    }

    /// <summary>Reads a nullable datetime column as a round-trip UTC string.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value in "o" format, or null.</returns>
    public static string? NullableUtc(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
    }

    /// <summary>Reads any numeric column as a double, or null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    public static double? Number(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a boolean column, or null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    /// <remarks>
    /// Neither Oracle nor Teradata has a bit type in the SQL Server sense, so a
    /// flag arrives as NUMBER(1) or BYTEINT. Zero is false and anything else is
    /// true, which is the convention both estates use.
    /// </remarks>
    public static bool? Flag(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetValue(ordinal);
        return value is bool flag ? flag : Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Reads an integer column.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    public static long? Integer(DbDataReader reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }
}
