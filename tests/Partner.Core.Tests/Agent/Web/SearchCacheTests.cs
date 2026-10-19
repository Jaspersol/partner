using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The answer cache. There is no long-lived tier here on purpose — a week-old result answering today's question
/// is wrong by construction — so these tests are mostly about the lifetimes, and about the two rules that keep a
/// cached answer from being worse than no answer: only cache something that was found, and always report its age.
/// </summary>
public sealed class SearchCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LifetimeOf_ShrinksAsTheQuestionGetsMoreTimeSensitive()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.News), false));
        Assert.Equal(TimeSpan.FromMinutes(5), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.General, SearchRecency.Day), false));
        Assert.Equal(TimeSpan.FromMinutes(15), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.General, SearchRecency.Week), false));
        Assert.Equal(TimeSpan.FromMinutes(30), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.General, SearchRecency.Month), false));
        Assert.Equal(TimeSpan.FromHours(1), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.General, SearchRecency.Year), false));
        Assert.Equal(TimeSpan.FromHours(1), SearchCache.LifetimeOf(new SearchQuery("x"), false));
    }

    [Fact]
    public void LifetimeOf_ALookedUpRecordHasItsOwnLifetime()
    {
        // A temperature is expired by its publisher's own clock, so the shortest lifetime we dare hold it for is
        // ten minutes whatever the question said.
        Assert.Equal(TimeSpan.FromMinutes(10), SearchCache.LifetimeOf(new SearchQuery("x"), recordAnswered: true));

        // The record rule is asked before the time-sensitive ones, so a record answer outlives a news answer. The
        // planner never puts a record engine in a news pool, so this pins the order of the checks rather than a
        // case a user can reach.
        Assert.Equal(TimeSpan.FromMinutes(10), SearchCache.LifetimeOf(new SearchQuery("x", SearchKinds.News), recordAnswered: true));
    }

    [Fact]
    public void WorthCaching_OnlyWhenSomethingWasFound()
    {
        // A search where every engine failed is a snapshot of this moment's network, not an answer. Caching one
        // would turn a transient outage into an hour of "nothing found".
        Assert.True(SearchCache.WorthCaching(Outcome(new SearchQuery("x"), Now)));
        Assert.False(SearchCache.WorthCaching(Outcome(new SearchQuery("x"), Now, found: false)));
    }

    [Fact]
    public void TryGet_ReportsHowOldTheAnswerIs()
    {
        var clock = new MutableClock(Now);
        var cache = new SearchCache(() => clock.Now);
        var query = new SearchQuery("anything");
        cache.Set(Outcome(query, Now));

        Assert.True(cache.TryGet(query.CacheKey, out var outcome, out var age));
        Assert.NotNull(outcome);

        clock.Now = Now.AddMinutes(2);
        Assert.True(cache.TryGet(query.CacheKey, out _, out age));
        Assert.Equal(TimeSpan.FromMinutes(2), age);
    }

    [Fact]
    public void TryGet_DropsAnEntryPastItsLifetime()
    {
        var clock = new MutableClock(Now);
        var cache = new SearchCache(() => clock.Now);
        var query = new SearchQuery("anything");
        cache.Set(Outcome(query, Now));

        clock.Now = Now.AddHours(2);

        Assert.False(cache.TryGet(query.CacheKey, out var outcome, out _));
        Assert.Null(outcome);

        // Dropped on the way past, not merely reported as stale.
        clock.Now = Now;
        Assert.False(cache.TryGet(query.CacheKey, out _, out _));
    }

    [Fact]
    public void TryGet_ExpiresARecordAnswerSoonerThanAnOrdinaryOne()
    {
        var record = FakeEngines.Make("open-meteo", ContentKinds.Weather, nature: ResultNature.Record);
        var clock = new MutableClock(Now);
        var cache = new SearchCache(() => clock.Now);
        var query = new SearchQuery("weather somewhere");

        cache.Set(Outcome(query, Now, attempts: [FakeEngines.Answered(record, FakeEngines.Hit("open-meteo", "21°C"))]));

        clock.Now = Now.AddMinutes(9);
        Assert.True(cache.TryGet(query.CacheKey, out _, out _));

        clock.Now = Now.AddMinutes(11);
        Assert.False(cache.TryGet(query.CacheKey, out _, out _));
    }

    [Fact]
    public void TryGet_IsKeyedByTheWholeQuestion()
    {
        var cache = new SearchCache(() => Now);
        cache.Set(Outcome(new SearchQuery("rust", SearchKinds.General, null, 8), Now));

        // A cached eight-result answer is not an answer to a request for twenty.
        Assert.False(cache.TryGet(new SearchQuery("rust", SearchKinds.General, null, 20).CacheKey, out _, out _));
        Assert.False(cache.TryGet(new SearchQuery("rust", SearchKinds.General, SearchRecency.Day, 8).CacheKey, out _, out _));
        Assert.False(cache.TryGet(new SearchQuery("cargo", SearchKinds.General, null, 8).CacheKey, out _, out _));
    }

    [Fact]
    public void TryGet_IgnoresSurroundingWhitespaceInTheQuery()
    {
        var cache = new SearchCache(() => Now);
        cache.Set(Outcome(new SearchQuery("rust", SearchKinds.General), Now));

        // The same question typed with a trailing space is the same question.
        var key = new SearchQuery(" rust ", SearchKinds.General).CacheKey;
        Assert.Equal(new SearchQuery("rust", SearchKinds.General).CacheKey, key);
        Assert.True(cache.TryGet(key, out _, out _));
    }

    [Fact]
    public void Set_KeepsTheWholeOutcomeNotJustTheHits()
    {
        var cache = new SearchCache(() => Now);
        var query = new SearchQuery("anything");
        var outcome = Outcome(query, Now);
        cache.Set(outcome);

        Assert.True(cache.TryGet(query.CacheKey, out var cached, out _));

        // The metadata is part of the answer: a cached result that forgot which engines were silent would tell
        // the model something different from what the same search said a minute ago.
        Assert.Same(outcome, cached);
    }

    [Fact]
    public void Set_EvictsTheLeastRecentlyUsedAtCapacity()
    {
        var cache = new SearchCache(() => Now);

        for (var index = 0; index <= SearchCache.Capacity; index++)
        {
            cache.Set(Outcome(new SearchQuery($"q{index}"), Now));
        }

        Assert.False(cache.TryGet(new SearchQuery("q0").CacheKey, out _, out _));
        Assert.True(cache.TryGet(new SearchQuery($"q{SearchCache.Capacity}").CacheKey, out _, out _));
    }

    [Fact]
    public void Set_ReadsProtectAnEntryFromEviction()
    {
        var cache = new SearchCache(() => Now);

        for (var index = 0; index < SearchCache.Capacity; index++)
        {
            cache.Set(Outcome(new SearchQuery($"q{index}"), Now));
        }

        // Asking again is what a cache exists for; an entry that gets re-read is not the least recently used one.
        Assert.True(cache.TryGet(new SearchQuery("q0").CacheKey, out _, out _));

        cache.Set(Outcome(new SearchQuery("overflow"), Now));

        Assert.False(cache.TryGet(new SearchQuery("q1").CacheKey, out _, out _));
        Assert.True(cache.TryGet(new SearchQuery("q0").CacheKey, out _, out _));
    }

    [Fact]
    public void Clear_EmptiesIt()
    {
        var cache = new SearchCache(() => Now);
        var query = new SearchQuery("anything");
        cache.Set(Outcome(query, Now));

        cache.Clear();

        Assert.False(cache.TryGet(query.CacheKey, out _, out _));
    }

    private static SearchOutcome Outcome(
        SearchQuery query,
        DateTimeOffset at,
        bool found = true,
        IReadOnlyList<EngineAttempt>? attempts = null) =>
        new(
            query,
            found ? [FakeEngines.Hit("duckduckgo", "a result")] : [],
            attempts ?? [],
            [],
            [],
            TimeSpan.FromMilliseconds(120),
            at,
            BudgetExpired: false);

    private sealed class MutableClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }
}
