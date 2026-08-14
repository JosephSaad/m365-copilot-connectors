// ---------------------------------------------------------------------------
// SqlErrorClassifier.cs
// Turns a SqlException into a decision: refresh the secret and retry once,
// ask the platform to retry with backoff, or fail the crawl.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Sql
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Data.SqlClient;

    /// <summary>Coarse classification used for metrics and for the retry decision.</summary>
    public enum SqlFailureCategory
    {
        /// <summary>Not a SQL failure.</summary>
        None = 0,

        /// <summary>Login or permission failure. Refresh the secret and retry once.</summary>
        Authentication = 1,

        /// <summary>Timeout, deadlock or connection reset. Worth an exponential backoff retry.</summary>
        Transient = 2,

        /// <summary>Anything else from the data source, for example a missing column.</summary>
        DataSource = 3,
    }

    /// <summary>
    /// Classifies SQL Server errors by number.
    /// </summary>
    public static class SqlErrorClassifier
    {
        // Login failed, password expired, cannot open database, and the Azure SQL
        // equivalents. A rotation lands here, which is why these trigger the single
        // secret refresh retry.
        private static readonly HashSet<int> AuthenticationErrorNumbers = new HashSet<int>
        {
            4060,   // Cannot open database requested by the login.
            18450,  // Login failed: login is valid but database access failed.
            18452,  // Login failed: untrusted domain, cannot be used with Windows authentication.
            18456,  // Login failed for user.
            18461,  // Login failed: server is in single user mode.
            18486,  // Login failed: account is locked out.
            18487,  // Login failed: password expired.
            18488,  // Login failed: password must be changed.
            40615,  // Azure SQL: cannot open server, firewall rule.
        };

        // Documented transient fault numbers. The platform retries with the
        // ExponentialBackOff policy returned in RetryDetails.
        private static readonly HashSet<int> TransientErrorNumbers = new HashSet<int>
        {
            -2,     // Timeout expired.
            20,     // Instance does not support encryption / transport level error.
            64,     // A connection was successfully established but then failed.
            121,    // Semaphore timeout.
            233,    // No process on the other end of the pipe.
            617,    // Descriptor for object in database not found.
            921,    // Database has not been recovered yet.
            997,    // Asynchronous operation in progress.
            1203,   // Process does not own lock.
            1204,   // Lock resources unavailable.
            1205,   // Deadlock victim.
            1221,   // Deadlock monitor could not resolve.
            1222,   // Lock request timeout.
            4221,   // Login to read secondary failed.
            8645,   // Timeout waiting for memory resource.
            8651,   // Low memory condition.
            10053,  // Transport level error on send.
            10054,  // Existing connection forcibly closed.
            10060,  // Network error on connect.
            10928,  // Azure SQL: resource limit reached.
            10929,  // Azure SQL: server too busy.
            40197,  // Azure SQL: service error processing the request.
            40501,  // Azure SQL: service is busy.
            40613,  // Azure SQL: database unavailable.
            49918,  // Azure SQL: cannot process request, not enough resources.
            49919,  // Azure SQL: too many create or update operations.
            49920,  // Azure SQL: too many operations in progress.
        };

        /// <summary>Classifies an exception.</summary>
        public static SqlFailureCategory Classify(Exception exception)
        {
            SqlException sql = Unwrap(exception);
            if (sql == null)
            {
                return SqlFailureCategory.None;
            }

            foreach (int number in Numbers(sql))
            {
                if (AuthenticationErrorNumbers.Contains(number))
                {
                    return SqlFailureCategory.Authentication;
                }
            }

            foreach (int number in Numbers(sql))
            {
                if (TransientErrorNumbers.Contains(number))
                {
                    return SqlFailureCategory.Transient;
                }
            }

            return SqlFailureCategory.DataSource;
        }

        /// <summary>True when the failure is a login or permission problem.</summary>
        public static bool IsAuthenticationFailure(Exception exception)
        {
            return Classify(exception) == SqlFailureCategory.Authentication;
        }

        /// <summary>True when the failure is worth retrying with backoff.</summary>
        public static bool IsTransient(Exception exception)
        {
            return Classify(exception) == SqlFailureCategory.Transient;
        }

        /// <summary>Returns the SqlException in the chain, or null.</summary>
        public static SqlException Unwrap(Exception exception)
        {
            Exception current = exception;

            while (current != null)
            {
                var sql = current as SqlException;
                if (sql != null)
                {
                    return sql;
                }

                current = current.InnerException;
            }

            return null;
        }

        private static IEnumerable<int> Numbers(SqlException exception)
        {
            yield return exception.Number;

            foreach (SqlError error in exception.Errors)
            {
                yield return error.Number;
            }
        }
    }
}
