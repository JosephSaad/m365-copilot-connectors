// ---------------------------------------------------------------------------
// SecretRefreshRetryPolicy.cs
// One place implementing the rotation rule: on an authentication failure, drop
// the cached secret and retry the operation exactly once.
//
// Exactly once, not "until it works": a genuinely wrong credential must surface
// as an error rather than turning into a retry storm against the directory or
// the SQL instance, which looks like a brute force attempt to a SIEM.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Secrets
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;

    /// <summary>
    /// Runs an operation that depends on a secret, refreshing the secret once if
    /// the operation fails authentication.
    /// </summary>
    public sealed class SecretRefreshRetryPolicy
    {
        private readonly ISecretProvider secrets;
        private readonly ILogger logger;

        /// <summary>Initializes the policy.</summary>
        public SecretRefreshRetryPolicy(ISecretProvider secrets, ILogger logger)
        {
            if (secrets == null)
            {
                throw new ArgumentNullException(nameof(secrets));
            }

            this.secrets = secrets;
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>Gets the number of retries performed. Exposed for tests and metrics.</summary>
        public int RetryCount { get; private set; }

        /// <summary>
        /// Executes <paramref name="operation"/>. If it throws and
        /// <paramref name="isAuthenticationFailure"/> classifies the exception as an
        /// authentication failure, the named secret is invalidated and the
        /// operation is attempted once more. Any second failure is surfaced.
        /// </summary>
        /// <param name="secretName">
        /// The secret to invalidate, or null when the operation does not depend on
        /// one (for example Windows integrated authentication). With null, no retry
        /// is attempted because there is nothing to refresh.
        /// </param>
        public async Task<T> ExecuteAsync<T>(
            string secretName,
            Func<CancellationToken, Task<T>> operation,
            Func<Exception, bool> isAuthenticationFailure,
            CancellationToken ct)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (isAuthenticationFailure == null)
            {
                throw new ArgumentNullException(nameof(isAuthenticationFailure));
            }

            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) &&
                                       isAuthenticationFailure(ex) &&
                                       !string.IsNullOrWhiteSpace(secretName))
            {
                this.logger.Warning(
                    "Authentication failed using secret {SecretName}. Invalidating the cached value and retrying once. " +
                    "This is the expected path immediately after a credential rotation.",
                    secretName);

                await this.secrets.InvalidateAsync(secretName, ct).ConfigureAwait(false);
                this.RetryCount++;

                // Exactly one retry. A second failure is a real problem and is surfaced.
                return await operation(ct).ConfigureAwait(false);
            }
        }

        /// <summary>Void-returning overload of <see cref="ExecuteAsync{T}"/>.</summary>
        public async Task ExecuteAsync(
            string secretName,
            Func<CancellationToken, Task> operation,
            Func<Exception, bool> isAuthenticationFailure,
            CancellationToken ct)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            await this.ExecuteAsync<bool>(
                secretName,
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                isAuthenticationFailure,
                ct).ConfigureAwait(false);
        }
    }
}
