// ---------------------------------------------------------------------------
// GraphThrottling.cs
// Reading Retry-After off a 429.
//
// Guessing is worse than being told, and guessing low is what turns one 429
// into a run of them. Separated from the write loop so it can be tested: a
// header parser that has never been exercised is a header parser that silently
// falls back to the guess.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Globalization;
using Microsoft.Graph.Models.ODataErrors;

/// <summary>Backoff decisions for a throttled write.</summary>
public static class GraphThrottling
{
    /// <summary>Longest wait honoured from a Retry-After header.</summary>
    public const int MaxRetryAfterSeconds = 300;

    /// <summary>Reads Retry-After from an error's headers.</summary>
    /// <param name="error">The Graph error.</param>
    /// <returns>The wait the service asked for, or null when it did not say.</returns>
    public static TimeSpan? RetryAfter(ODataError? error)
    {
        if (error?.ResponseHeaders is null)
        {
            return null;
        }

        foreach (var header in error.ResponseHeaders)
        {
            if (!string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string value in header.Value)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) &&
                    seconds > 0)
                {
                    return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds));
                }

                // RFC 9110 also allows an HTTP-date. Falling back to the guess on
                // a date the service actually sent is guessing low on purpose.
                if (DateTimeOffset.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset when))
                {
                    double delta = (when - DateTimeOffset.UtcNow).TotalSeconds;

                    if (delta > 0)
                    {
                        return TimeSpan.FromSeconds(Math.Min(delta, MaxRetryAfterSeconds));
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The wait to use when the service sent no Retry-After.</summary>
    /// <param name="attempt">The attempt that just failed, from 1.</param>
    /// <returns>An exponential backoff, capped at a minute.</returns>
    public static TimeSpan Backoff(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
    }
}
