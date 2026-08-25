// ---------------------------------------------------------------------------
// CachingSecretProvider.cs
// In-memory TTL cache in front of any ISecretProvider.
//
// This class is what makes credential rotation work without a service restart:
// a cached value lives at most SecretCacheTtlMinutes, and any authentication
// failure drops it immediately through InvalidateAsync so the retry picks up the
// rotated value. Nothing is ever written to disk.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Secrets
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;

    /// <summary>
    /// Caches resolved secrets in process memory for a configurable time to live.
    /// </summary>
    public sealed class CachingSecretProvider : ISecretProvider, IDisposable
    {
        private readonly ISecretProvider inner;
        private readonly TimeSpan timeToLive;
        private readonly ILogger logger;
        private readonly TimeProvider time;
        private readonly ConcurrentDictionary<string, CacheEntry> entries =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        private readonly SemaphoreSlim fetchGate = new SemaphoreSlim(1, 1);
        private bool disposed;

        /// <summary>Initializes the cache.</summary>
        /// <param name="inner">The provider that does the real resolution.</param>
        /// <param name="timeToLive">How long a value may be reused.</param>
        /// <param name="logger">Log destination. Cache misses are logged at Warning.</param>
        /// <param name="timeProvider">Clock seam so tests can advance time without sleeping.</param>
        public CachingSecretProvider(
            ISecretProvider inner,
            TimeSpan timeToLive,
            ILogger logger,
            TimeProvider timeProvider = null)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner));
            }

            if (timeToLive <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeToLive), "Time to live must be positive.");
            }

            this.inner = inner;
            this.timeToLive = timeToLive;
            this.logger = logger ?? Log.Logger;
            this.time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Gets the number of secrets currently held. Exposed for tests and diagnostics.</summary>
        public int CachedCount
        {
            get { return this.entries.Count; }
        }

        /// <inheritdoc />
        public async Task<string> GetSecretAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Secret name is required.", nameof(name));
            }

            DateTimeOffset now = this.time.GetUtcNow();

            CacheEntry entry;
            if (this.entries.TryGetValue(name, out entry) && entry.ExpiresAtUtc > now)
            {
                return entry.Value;
            }

            await this.fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check: another caller may have populated the entry while this
                // one waited on the gate.
                now = this.time.GetUtcNow();
                if (this.entries.TryGetValue(name, out entry) && entry.ExpiresAtUtc > now)
                {
                    return entry.Value;
                }

                this.logger.Warning(
                    "Secret cache miss for {SecretName}. Resolving from the configured secret source.",
                    name);

                string value = await this.inner.GetSecretAsync(name, ct).ConfigureAwait(false);

                this.entries[name] = new CacheEntry(value, this.time.GetUtcNow().Add(this.timeToLive));
                return value;
            }
            finally
            {
                this.fetchGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task InvalidateAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            CacheEntry removed;
            if (this.entries.TryRemove(name, out removed))
            {
                this.logger.Warning(
                    "Dropped cached secret {SecretName}. The next use resolves it again from the secret source.",
                    name);
            }

            await this.inner.InvalidateAsync(name, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.entries.Clear();
            this.fetchGate.Dispose();
        }

        private sealed class CacheEntry
        {
            public CacheEntry(string value, DateTimeOffset expiresAtUtc)
            {
                this.Value = value;
                this.ExpiresAtUtc = expiresAtUtc;
            }

            public string Value { get; }

            public DateTimeOffset ExpiresAtUtc { get; }
        }
    }
}
