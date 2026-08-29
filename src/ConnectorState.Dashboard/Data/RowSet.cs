// ---------------------------------------------------------------------------
// RowSet.cs
// Column access by name, with the nulls handled explicitly.
//
// Every mapping in CrawlStateQueries goes through this, and the reason is
// ordinals. reader.GetInt32(11) is correct until somebody inserts a column into
// a procedure in sql/24, at which point it is silently correct-looking and
// wrong: the run list shows ItemsSkipped in the ItemsDeleted column and nobody
// notices, because both are plausible small integers. Names break loudly
// instead - a missing column throws here, naming the column, on the first
// request after the deployment rather than during the incident it caused.
//
// The ordinal map is built once per result set from the reader's own metadata,
// so this is a dictionary lookup per column and not a linear scan.
//
// The nullable accessors are separate methods rather than a nullable return on
// every one, so that a column the schema says is NOT NULL is read with a method
// that will throw if it ever is. That is the failure worth having: a null in a
// non-nullable column means the view changed underneath us, and a silent
// default would hide it behind a plausible zero.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Data;

using Microsoft.Data.SqlClient;

/// <summary>Reads the current row of a <see cref="SqlDataReader"/> by column name.</summary>
internal sealed class RowSet
{
    private readonly SqlDataReader reader;
    private readonly Dictionary<string, int> ordinals;

    /// <summary>Initializes a new instance of the <see cref="RowSet"/> class.</summary>
    /// <param name="reader">A reader positioned on a result set. Its metadata is read immediately.</param>
    public RowSet(SqlDataReader reader)
    {
        this.reader = reader;
        this.ordinals = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < reader.FieldCount; i++)
        {
            // Last one wins. A duplicate column name in a result set is a defect
            // in the procedure, not something to fail a page render over.
            this.ordinals[reader.GetName(i)] = i;
        }
    }

    /// <summary>Reads a non-null string column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public string Text(string name) => this.reader.GetString(this.Ordinal(name));

    /// <summary>Reads a string column that the schema allows to be null.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value, or null.</returns>
    public string? TextOrNull(string name)
    {
        int ordinal = this.Ordinal(name);
        return this.reader.IsDBNull(ordinal) ? null : this.reader.GetString(ordinal);
    }

    /// <summary>Reads a non-null 32-bit integer column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public int Int32(string name) => this.reader.GetInt32(this.Ordinal(name));

    /// <summary>Reads a 32-bit integer column that the schema allows to be null.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value, or null.</returns>
    public int? Int32OrNull(string name)
    {
        int ordinal = this.Ordinal(name);
        return this.reader.IsDBNull(ordinal) ? null : this.reader.GetInt32(ordinal);
    }

    /// <summary>Reads a non-null 64-bit integer column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public long Int64(string name) => this.reader.GetInt64(this.Ordinal(name));

    /// <summary>Reads a 64-bit integer column that the schema allows to be null.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value, or null.</returns>
    public long? Int64OrNull(string name)
    {
        int ordinal = this.Ordinal(name);
        return this.reader.IsDBNull(ordinal) ? null : this.reader.GetInt64(ordinal);
    }

    /// <summary>Reads a non-null bit column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public bool Bool(string name) => this.reader.GetBoolean(this.Ordinal(name));

    /// <summary>Reads a decimal column. Every decimal in sql/24 is a computed ratio and may be null.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value, or null.</returns>
    public decimal? DecimalOrNull(string name)
    {
        int ordinal = this.Ordinal(name);
        return this.reader.IsDBNull(ordinal) ? null : this.reader.GetDecimal(ordinal);
    }

    /// <summary>Reads a non-null decimal column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public decimal Decimal(string name) => this.reader.GetDecimal(this.Ordinal(name));

    /// <summary>Reads a non-null datetime2 column. The value is UTC; the schema stores nothing else.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value.</returns>
    public DateTime Time(string name) => this.reader.GetDateTime(this.Ordinal(name));

    /// <summary>Reads a datetime2 column that the schema allows to be null.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The value, or null.</returns>
    public DateTime? TimeOrNull(string name)
    {
        int ordinal = this.Ordinal(name);
        return this.reader.IsDBNull(ordinal) ? null : this.reader.GetDateTime(ordinal);
    }

    private int Ordinal(string name)
    {
        if (this.ordinals.TryGetValue(name, out int ordinal))
        {
            return ordinal;
        }

        // Naming the column and listing what did come back turns "the dashboard
        // is broken" into "sql/24 was deployed without sql/22" in one glance.
        throw new InvalidOperationException(
            $"The result set has no column named '{name}'. It returned: " +
            string.Join(", ", this.ordinals.Keys) +
            ". The database schema and this build disagree; redeploy sql/22 and sql/24.");
    }
}
