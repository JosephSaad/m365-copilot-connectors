// ---------------------------------------------------------------------------
// SqlTicketSource.cs
// The production ITicketSource. Opens connections through the shared
// SqlConnectionFactory, so the encryption rules, the authentication mode and the
// secret refresh retry are the same ones the console app uses.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Serilog;
    using SqlTicketsConnector.Logging;
    using SqlConnector.Security.Configuration;
    using SqlConnector.Security.Sql;

    /// <summary>Reads dbo.Tickets over a SqlConnection.</summary>
    public sealed class SqlTicketSource : ITicketSource
    {
        private readonly SqlConnectionFactory connections;
        private readonly DataSourceOptions options;
        private readonly CrawlMetrics metrics;
        private readonly ILogger logger;

        /// <summary>Initializes the source.</summary>
        public SqlTicketSource(
            SqlConnectionFactory connections,
            DataSourceOptions options,
            CrawlMetrics metrics,
            ILogger logger)
        {
            if (connections == null)
            {
                throw new ArgumentNullException(nameof(connections));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.connections = connections;
            this.options = options;
            this.metrics = metrics;
            this.logger = logger ?? Log.Logger;
        }

        /// <inheritdoc />
        public async Task ValidateAsync(CancellationToken ct)
        {
            using (SqlConnection connection = await this.connections.OpenAsync(ct).ConfigureAwait(false))
            using (var command = new SqlCommand(SqlDataSource.ValidationQuery(this.options.SoftDeleteEnabled), connection))
            {
                command.CommandTimeout = this.options.ConnectTimeoutSeconds;
                this.RecordRoundTrip();
                await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<TicketRow> ReadAsync(
            Watermark from,
            TicketReadMode mode,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string query = mode == TicketReadMode.FullCrawl
                ? SqlDataSource.FullCrawlQuery(this.options.SoftDeleteEnabled)
                : SqlDataSource.IncrementalCrawlQuery(this.options.SoftDeleteEnabled);

            using (SqlConnection connection = await this.connections.OpenAsync(ct).ConfigureAwait(false))
            using (var command = new SqlCommand(query, connection))
            {
                command.CommandTimeout = this.options.ConnectTimeoutSeconds;
                command.Parameters.Add(SqlDataSource.WatermarkTimeParameter, SqlDbType.DateTime2).Value =
                    from.LastModifiedUtc;
                command.Parameters.Add(SqlDataSource.WatermarkIdParameter, SqlDbType.Int).Value = from.TicketId;

                this.RecordRoundTrip();

                using (SqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    int ticketIdOrdinal = reader.GetOrdinal("TicketId");
                    int titleOrdinal = reader.GetOrdinal("Title");
                    int statusOrdinal = reader.GetOrdinal("Status");
                    int assignedToOrdinal = reader.GetOrdinal("AssignedTo");
                    int bodyOrdinal = reader.GetOrdinal("Body");
                    int lastModifiedOrdinal = reader.GetOrdinal("LastModified");
                    int isDeletedOrdinal = this.options.SoftDeleteEnabled ? reader.GetOrdinal("IsDeleted") : -1;

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();

                        yield return new TicketRow
                        {
                            TicketId = reader.GetInt32(ticketIdOrdinal),
                            Title = SafeString(reader, titleOrdinal),
                            Status = SafeString(reader, statusOrdinal),
                            AssignedTo = SafeString(reader, assignedToOrdinal),
                            Body = SafeString(reader, bodyOrdinal),
                            LastModifiedUtc = DateTime.SpecifyKind(
                                reader.GetDateTime(lastModifiedOrdinal),
                                DateTimeKind.Utc),
                            IsDeleted = isDeletedOrdinal >= 0 &&
                                        !reader.IsDBNull(isDeletedOrdinal) &&
                                        reader.GetBoolean(isDeletedOrdinal),
                        };
                    }
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Connections are opened and closed per operation; nothing is held.
        }

        private static string SafeString(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private void RecordRoundTrip()
        {
            if (this.metrics != null)
            {
                this.metrics.RecordSqlRoundTrip();
            }
        }
    }

    /// <summary>Creates <see cref="SqlTicketSource"/> instances.</summary>
    public sealed class SqlTicketSourceFactory : ITicketSourceFactory
    {
        private readonly SqlConnectionFactory connections;
        private readonly DataSourceOptions options;
        private readonly ILogger logger;

        /// <summary>Initializes the factory.</summary>
        public SqlTicketSourceFactory(SqlConnectionFactory connections, DataSourceOptions options, ILogger logger)
        {
            if (connections == null)
            {
                throw new ArgumentNullException(nameof(connections));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.connections = connections;
            this.options = options;
            this.logger = logger ?? Log.Logger;
        }

        /// <inheritdoc />
        public string Description
        {
            get { return this.connections.Description; }
        }

        /// <inheritdoc />
        public ITicketSource Create(CrawlMetrics metrics)
        {
            return new SqlTicketSource(this.connections, this.options, metrics, this.logger);
        }
    }
}
