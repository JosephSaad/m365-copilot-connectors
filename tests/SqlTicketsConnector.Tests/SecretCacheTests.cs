// ---------------------------------------------------------------------------
// SecretCacheTests.cs
// Control evidence for secret caching and credential rotation:
//   the cache honours its TTL,
//   invalidation drops the value immediately,
//   an authentication failure retries exactly once after a refresh.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;
    using Serilog.Core;
    using SqlConnector.Security.Secrets;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class SecretCacheTests
    {
        private static readonly ILogger Silent = Logger.None;

        [Fact]
        public async Task Cached_value_is_reused_inside_the_time_to_live()
        {
            var inner = new FakeSecretProvider("first", "second");
            var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

            using (var cache = new CachingSecretProvider(inner, TimeSpan.FromMinutes(60), Silent, clock))
            {
                Assert.Equal("first", await cache.GetSecretAsync("sql-password", CancellationToken.None));

                clock.Advance(TimeSpan.FromMinutes(59));

                Assert.Equal("first", await cache.GetSecretAsync("sql-password", CancellationToken.None));
                Assert.Equal(1, inner.GetCount);
            }
        }

        [Fact]
        public async Task Value_is_resolved_again_once_the_time_to_live_expires()
        {
            var inner = new FakeSecretProvider("first", "second");
            var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

            using (var cache = new CachingSecretProvider(inner, TimeSpan.FromMinutes(60), Silent, clock))
            {
                await cache.GetSecretAsync("sql-password", CancellationToken.None);

                clock.Advance(TimeSpan.FromMinutes(61));

                Assert.Equal("second", await cache.GetSecretAsync("sql-password", CancellationToken.None));
                Assert.Equal(2, inner.GetCount);
            }
        }

        [Fact]
        public async Task Invalidate_drops_the_cached_value_without_waiting_for_expiry()
        {
            var inner = new FakeSecretProvider("first", "rotated");
            var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

            using (var cache = new CachingSecretProvider(inner, TimeSpan.FromMinutes(60), Silent, clock))
            {
                await cache.GetSecretAsync("sql-password", CancellationToken.None);

                await cache.InvalidateAsync("sql-password", CancellationToken.None);

                Assert.Equal(0, cache.CachedCount);
                Assert.Equal("rotated", await cache.GetSecretAsync("sql-password", CancellationToken.None));
                Assert.Equal(2, inner.GetCount);
                Assert.Equal(1, inner.InvalidateCount);
            }
        }

        [Fact]
        public async Task Authentication_failure_invalidates_the_secret_and_retries_exactly_once()
        {
            var secrets = new FakeSecretProvider();
            var policy = new SecretRefreshRetryPolicy(secrets, Silent);

            int attempts = 0;

            string result = await policy.ExecuteAsync(
                "sql-password",
                ct =>
                {
                    attempts++;

                    if (attempts == 1)
                    {
                        // What a rotated password looks like to the first attempt.
                        throw new FakeAuthenticationFailure("Login failed for user.");
                    }

                    return Task.FromResult("connected");
                },
                ex => ex is FakeAuthenticationFailure,
                CancellationToken.None);

            Assert.Equal("connected", result);
            Assert.Equal(2, attempts);
            Assert.Equal(1, policy.RetryCount);
            Assert.Equal(1, secrets.InvalidateCount);
            Assert.Equal("sql-password", secrets.LastInvalidatedName);
        }

        [Fact]
        public async Task A_second_authentication_failure_is_surfaced_rather_than_retried_again()
        {
            var secrets = new FakeSecretProvider();
            var policy = new SecretRefreshRetryPolicy(secrets, Silent);

            int attempts = 0;

            await Assert.ThrowsAsync<FakeAuthenticationFailure>(() => policy.ExecuteAsync<string>(
                "sql-password",
                ct =>
                {
                    attempts++;
                    throw new FakeAuthenticationFailure("Login failed for user.");
                },
                ex => ex is FakeAuthenticationFailure,
                CancellationToken.None));

            // Exactly one retry: a genuinely wrong credential must not turn into a
            // retry storm that looks like a brute force attempt to the SIEM.
            Assert.Equal(2, attempts);
            Assert.Equal(1, policy.RetryCount);
        }

        [Fact]
        public async Task A_non_authentication_failure_is_not_retried()
        {
            var secrets = new FakeSecretProvider();
            var policy = new SecretRefreshRetryPolicy(secrets, Silent);

            int attempts = 0;

            await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<string>(
                "sql-password",
                ct =>
                {
                    attempts++;
                    throw new TimeoutException("Timeout expired.");
                },
                ex => ex is FakeAuthenticationFailure,
                CancellationToken.None));

            Assert.Equal(1, attempts);
            Assert.Equal(0, secrets.InvalidateCount);
        }
    }
}
