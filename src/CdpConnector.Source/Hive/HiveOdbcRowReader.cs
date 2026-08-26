// ---------------------------------------------------------------------------
// HiveOdbcRowReader.cs
// The twenty lines that actually talk to the Cloudera driver.
//
// Everything interesting about the Hive path - the ordering, the watermark, the
// mapping, the routing - is tested against IHiveRowReader with canned rows.
// This is the part that cannot be, so it is kept small enough to read.
//
// The connection string it is handed carries no credential: AuthMech=1 is
// Kerberos and UseOnlySSPI=1 tells the driver to authenticate from the Windows
// logon session of the account the service runs as. There is nothing here to
// resolve from a vault and nothing to redact, which is the point.
//
// One translation matters: an ODBC authentication failure has to leave here as
// PushSourceAuthenticationException, because the exit-code contract says a
// rejected identity is exit 3 and everything else in the data path is exit 4.
// SQLSTATE 28000 is the standard's "invalid authorization specification", and
// the Kerberos failures the driver reports arrive as a message rather than a
// code, which is why the text is matched as well.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hive;

using System.Data;
using System.Data.Odbc;
using System.Runtime.CompilerServices;
using PushCore;
using Serilog;

/// <summary>Reads rows from Hive or Impala over ODBC.</summary>
public sealed class HiveOdbcRowReader : IHiveRowReader
{
    private readonly string connectionString;
    private readonly int commandTimeoutSeconds;
    private readonly ILogger log;

    /// <summary>Initializes a new instance of the <see cref="HiveOdbcRowReader"/> class.</summary>
    /// <param name="connectionString">Built by <see cref="HiveConnectionStringFactory"/>; carries no credential.</param>
    /// <param name="commandTimeoutSeconds">Query timeout. Zero means unlimited.</param>
    /// <param name="log">Where to report progress.</param>
    public HiveOdbcRowReader(string connectionString, int commandTimeoutSeconds, ILogger log)
    {
        this.connectionString = connectionString;
        this.commandTimeoutSeconds = commandTimeoutSeconds;
        this.log = log;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HiveRow> QueryAsync(
        string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = new OdbcConnection(this.connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OdbcException ex) when (IsAuthenticationFailure(ex))
        {
            throw new PushSourceAuthenticationException(
                "Hive refused this identity. The service account needs a valid Kerberos ticket for the " +
                "cluster's realm - check that it is running as the intended account and that the realm still " +
                "trusts it. No password is involved: this connector authenticates over SSPI.",
                ex);
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = this.commandTimeoutSeconds;

        using OdbcDataReader reader = await command.ExecuteReaderAsync(cancellationToken) as OdbcDataReader
            ?? throw new InvalidOperationException("The ODBC driver returned no reader.");

        // Column names are read once rather than per row: at a million rows the
        // GetName calls are measurable, and they cannot change mid-result.
        string[] columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(columns.Length, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < columns.Length; i++)
            {
                values[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return new HiveRow(values);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Decides whether an ODBC failure is the cluster refusing this identity
    /// rather than the cluster being unwell.
    /// </summary>
    /// <param name="ex">The driver's exception.</param>
    /// <returns>True when this is a credential problem.</returns>
    public static bool IsAuthenticationFailure(OdbcException ex)
    {
        foreach (OdbcError error in ex.Errors)
        {
            if (error.SQLState is "28000" or "08004")
            {
                return true;
            }

            string message = error.Message ?? string.Empty;

            if (message.Contains("Kerberos", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("GSS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("SSPI", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
