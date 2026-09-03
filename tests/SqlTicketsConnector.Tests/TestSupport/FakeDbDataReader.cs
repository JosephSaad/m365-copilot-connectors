// ---------------------------------------------------------------------------
// FakeDbDataReader.cs
// A DbDataReader over an in-memory row, so a connector's MapRow can be tested
// without a database.
//
// It exists because MapRow is where a provider's type surprises land. Oracle's
// NUMBER arrives as a decimal and Teradata's BYTEINT as a byte, so a mapping
// that compiles against DbDataReader can still throw the first time a real row
// reaches it - as exit 4, one row into a live crawl, with the ordinal-only
// message that deliberately will not say what the value was. Being able to hand
// MapRow a decimal in an integer column is the point of this class.
//
// Only the members the connectors call are implemented. The rest throw, so a
// test that strays outside the contract fails loudly rather than reading a
// default.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;

    /// <summary>A single-row DbDataReader built from a dictionary.</summary>
    public sealed class FakeDbDataReader : DbDataReader
    {
        private readonly List<string> names;
        private readonly List<object?> values;

        /// <summary>Initializes a new instance of the <see cref="FakeDbDataReader"/> class.</summary>
        /// <param name="row">Column name to value; a null value reads as DBNull.</param>
        public FakeDbDataReader(IDictionary<string, object?> row)
        {
            this.names = row.Keys.ToList();
            this.values = row.Values.ToList();
        }

        /// <inheritdoc/>
        public override int FieldCount => this.names.Count;

        /// <inheritdoc/>
        public override bool HasRows => true;

        /// <inheritdoc/>
        public override bool IsClosed => false;

        /// <inheritdoc/>
        public override int RecordsAffected => 0;

        /// <inheritdoc/>
        public override int Depth => 0;

        /// <inheritdoc/>
        public override object this[int ordinal] => this.GetValue(ordinal);

        /// <inheritdoc/>
        public override object this[string name] => this.GetValue(this.GetOrdinal(name));

        /// <inheritdoc/>
        public override int GetOrdinal(string name)
        {
            int index = this.names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

            return index >= 0
                ? index
                : throw new IndexOutOfRangeException(
                    $"The fake row has no column '{name}'. Columns: {string.Join(", ", this.names)}.");
        }

        /// <inheritdoc/>
        public override bool IsDBNull(int ordinal) =>
            this.values[ordinal] is null or DBNull;

        /// <inheritdoc/>
        public override object GetValue(int ordinal) =>
            this.values[ordinal] ?? DBNull.Value;

        /// <inheritdoc/>
        public override string GetString(int ordinal) =>
            (string)this.GetValue(ordinal);

        /// <inheritdoc/>
        public override DateTime GetDateTime(int ordinal) =>
            Convert.ToDateTime(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override int GetInt32(int ordinal) =>
            Convert.ToInt32(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override long GetInt64(int ordinal) =>
            Convert.ToInt64(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override bool GetBoolean(int ordinal) =>
            Convert.ToBoolean(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override double GetDouble(int ordinal) =>
            Convert.ToDouble(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override decimal GetDecimal(int ordinal) =>
            Convert.ToDecimal(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override string GetName(int ordinal) => this.names[ordinal];

        /// <inheritdoc/>
        public override Type GetFieldType(int ordinal) =>
            this.values[ordinal]?.GetType() ?? typeof(object);

        /// <inheritdoc/>
        public override string GetDataTypeName(int ordinal) => this.GetFieldType(ordinal).Name;

        /// <inheritdoc/>
        public override bool Read() => false;

        /// <inheritdoc/>
        public override bool NextResult() => false;

        /// <inheritdoc/>
        public override IEnumerator GetEnumerator() => this.values.GetEnumerator();

        /// <inheritdoc/>
        public override int GetValues(object[] values)
        {
            throw new NotSupportedException("Not part of the contract these connectors use.");
        }

        /// <inheritdoc/>
        public override byte GetByte(int ordinal) =>
            Convert.ToByte(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override char GetChar(int ordinal) =>
            Convert.ToChar(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override short GetInt16(int ordinal) =>
            Convert.ToInt16(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override float GetFloat(int ordinal) =>
            Convert.ToSingle(this.GetValue(ordinal), CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public override Guid GetGuid(int ordinal) => (Guid)this.GetValue(ordinal);

        /// <inheritdoc/>
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length)
        {
            throw new NotSupportedException("Not part of the contract these connectors use.");
        }

        /// <inheritdoc/>
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length)
        {
            throw new NotSupportedException("Not part of the contract these connectors use.");
        }
    }
}
