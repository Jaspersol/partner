namespace Partner.Core.Agent.Web;

/// <summary>
/// One search's complete result, including how it went. Cached whole rather than as hits alone, because the
/// metadata is part of the answer: a cached result that forgot which engines were silent would tell the model
/// something different from what the same search said a minute ago.
/// </summary>
public sealed record SearchOutcome(
    SearchQuery Query,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<EngineAttempt> Attempts,
    IReadOnlyList<BatchVerdict> Batches,
    IReadOnlyList<string> Skipped,
    TimeSpan Elapsed,
    DateTimeOffset At,
    bool BudgetExpired);

/// <summary>
/// An in-process answer cache, small and short-lived on purpose.
/// <para>
/// There is no long-lived tier. A week-old result answered today's question is wrong by construction, so the
/// lifetime shrinks as the question gets more time-sensitive, and the shortest lifetime wins. A hit must also
/// report its age — a cached answer that presents itself as fresh is exactly the failure this whole capability
/// exists to avoid.
/// </para>
/// </summary>
public sealed class SearchCache
{
    /// <summary>Entries kept. A voice session asks a handful of questions; two hundred is generosity, not a design.</summary>
    public const int Capacity = 200;

    /// <summary>A question about today, or the news. Five minutes, because the answer to "latest" stops being true.</summary>
    public static readonly TimeSpan TimeSensitiveLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A looked-up record. Its publisher's own clock expires it; this is the shortest we dare hold it.</summary>
    public static readonly TimeSpan RecordLifetime = TimeSpan.FromMinutes(10);

    /// <summary>An ordinary question. An hour is long enough to make a repeated question free and short enough to notice.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private readonly Dictionary<string, SearchOutcome> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private long _sequence;
    private readonly Func<DateTimeOffset> _clock;

    public SearchCache(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// How long an answer to this query may be reused. The shortest rule that applies wins, and a search that
    /// asked a record engine is treated as time-sensitive even when the query says nothing about it.
    /// </summary>
    public static TimeSpan LifetimeOf(SearchQuery query, bool recordAnswered)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (recordAnswered)
        {
            return RecordLifetime;
        }

        if (query.Kind == SearchKinds.News || query.Recency == SearchRecency.Day)
        {
            return TimeSensitiveLifetime;
        }

        return query.Recency switch
        {
            SearchRecency.Week => TimeSpan.FromMinutes(15),
            SearchRecency.Month => TimeSpan.FromMinutes(30),
            _ => DefaultLifetime,
        };
    }

    /// <summary>
    /// Whether an outcome is worth remembering at all. A search where every engine failed is a snapshot of this
    /// moment's network, not an answer: caching it would turn a transient outage into an hour of "nothing found".
    /// </summary>
    public static bool WorthCaching(SearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Hits.Count > 0;
    }

    /// <summary>Looks up a query. Expired entries are dropped on the way past.</summary>
    /// <param name="key">The query's cache key.</param>
    /// <param name="outcome">The cached outcome.</param>
    /// <param name="age">How old it is, which the answer must state.</param>
    public bool TryGet(string key, out SearchOutcome? outcome, out TimeSpan age)
    {
        ArgumentNullException.ThrowIfNull(key);

        var now = _clock();

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                age = now - cached.At;
                var lifetime = LifetimeOf(cached.Query, cached.Attempts.Any(attempt => attempt.Answered
                    && attempt.Engine.Traits.Nature == ResultNature.Record));

                if (age <= lifetime)
                {
                    _lastUsed[key] = ++_sequence;
                    outcome = cached;
                    return true;
                }

                _entries.Remove(key);
                _lastUsed.Remove(key);
            }
        }

        outcome = null;
        age = TimeSpan.Zero;
        return false;
    }

    public void Set(SearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        lock (_gate)
        {
            var key = outcome.Query.CacheKey;
            _entries[key] = outcome;
            _lastUsed[key] = ++_sequence;

            while (_entries.Count > Capacity)
            {
                // The oldest touch goes. With a capacity this small the scan costs nothing, and an LRU list would
                // be one more invariant to keep correct for no measurable gain.
                var oldest = _lastUsed.OrderBy(pair => pair.Value).First().Key;
                _entries.Remove(oldest);
                _lastUsed.Remove(oldest);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lastUsed.Clear();
        }
    }
}
