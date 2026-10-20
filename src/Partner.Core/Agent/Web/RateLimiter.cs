namespace Partner.Core.Agent.Web;

/// <summary>
/// A token bucket per engine, kept in memory.
/// <para>
/// It never blocks the fanout. The buckets refill on their own and a caller that cannot get a token is told how
/// long it would have to wait — the fanout then decides whether that wait is worth adding to everyone else's
/// answer, and for a breadth engine it usually is not.
/// </para>
/// </summary>
public sealed class RateLimiter
{
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private sealed class Bucket
    {
        public double Tokens { get; set; }

        public DateTimeOffset RefilledAt { get; set; }
    }

    /// <summary>
    /// Takes one token for an engine, or says how long one would take to appear. <see cref="TimeSpan.Zero"/> means
    /// the engine may be asked now.
    /// </summary>
    /// <param name="engineId">Which bucket. Buckets are per engine, not per query.</param>
    /// <param name="limit">The engine's declared rate. A rate of zero or less means unlimited.</param>
    /// <param name="now">The clock, passed in so the refill is testable without waiting a minute.</param>
    public TimeSpan Take(string engineId, RateLimit limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(engineId);
        ArgumentNullException.ThrowIfNull(limit);

        if (limit.PerMinute <= 0)
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (!_buckets.TryGetValue(engineId, out var bucket))
            {
                // A new engine starts full: the first search of a session must not be the one that gets throttled.
                bucket = new Bucket { Tokens = limit.PerMinute, RefilledAt = now };
                _buckets[engineId] = bucket;
            }

            var perSecond = limit.PerMinute / 60.0;
            var elapsed = (now - bucket.RefilledAt).TotalSeconds;

            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(limit.PerMinute, bucket.Tokens + (elapsed * perSecond));
                bucket.RefilledAt = now;
            }

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((1 - bucket.Tokens) / perSecond);
        }
    }
}
