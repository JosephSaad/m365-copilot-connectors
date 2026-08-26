// ---------------------------------------------------------------------------
// SqlRead.cs
// Reading a column without writing the null check four times per property.
//
// Every one of these treats DBNull as absent rather than as an error, because
// in a flattened hierarchy most columns are null on most rows: a customer has
// no consultant and no hours. The Utc pair stamps DateTimeKind rather than
// converting - the column is documented as UTC, and converting a value that is
// already UTC is how a timestamp drifts by the offset at each DST change.
// ---------------------------------------------------------------------------

namespace PushCore.Sql;

using Microsoft.Data.SqlClient;

/// <summary>Column readers shared by every connector.</summary>
public static class SqlRead
{
    /// <summary>Reads a string column, or empty when null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or an empty string.</returns>
    public static string Text(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    /// <summary>Reads a datetime column as a round-trip UTC string.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value in "o" format.</returns>
    public static string Utc(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
    }

    /// <summary>Reads a nullable datetime column as a round-trip UTC string.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value in "o" format, or null.</returns>
    public static string? NullableUtc(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
    }

    /// <summary>Reads any numeric column as a double, or null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    public static double? Number(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));
    }

    /// <summary>Reads a bit column, or null.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    public static bool? Flag(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    /// <summary>Reads an integer column.</summary>
    /// <param name="reader">Positioned on a row.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The value, or null.</returns>
    public static int? Integer(SqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
