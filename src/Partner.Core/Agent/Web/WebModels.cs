namespace Partner.Core.Agent.Web;

/// <summary>
/// Which content a source actually indexes. Declared by the engine and read by the planner, so "ask the right
/// people" is a property of the engine rather than a list somebody maintains elsewhere and forgets.
/// </summary>
[Flags]
public enum ContentKinds
{
    None = 0,
    General = 1,
    News = 2,
    Academic = 4,
    Code = 8,
    Encyclopedia = 16,
    Weather = 32,
    Qa = 64,
}

/// <summary>
/// Which scripts a source can answer a query written in. Flags rather than a single value because a source may
/// handle both, and the planner asks "does it cover the script I need".
/// </summary>
[Flags]
public enum QueryScripts
{
    Latin = 1,
    Cjk = 2,
    Any = Latin | Cjk,
}

/// <summary>Which scripts a query is written in, decided from its characters and nothing else.</summary>
public static class QueryScript
{
    /// <summary>
    /// CJK ideographs, kana and Hangul. Deliberately not "non-ASCII": accented Latin, Cyrillic and Greek are all
    /// Latin-indexed by every source here, and treating them as a Chinese query would route them badly.
    /// </summary>
    public static QueryScripts Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return QueryScripts.Any;
        }

        var cjk = false;
        var latin = false;

        foreach (var character in text)
        {
            if (character is >= '一' and <= '鿿'     // CJK unified ideographs
                or >= '㐀' and <= '䶿'               // extension A
                or >= '぀' and <= 'ヿ'               // kana
                or >= '가' and <= '힯'               // Hangul
                or >= '豈' and <= '﫿')              // compatibility ideographs
            {
                cjk = true;
            }
            else if (char.IsLetter(character) && character < 0x0250)
            {
                latin = true;
            }
        }

        return (cjk, latin) switch
        {
            (true, false) => QueryScripts.Cjk,
            (false, true) => QueryScripts.Latin,

            // Digits and punctuation only ("3.14", "gpt-5"), or mixed. Either way no source may be filtered out
            // on account of script — there is no evidence to filter on.
            _ => QueryScripts.Any,
        };
    }

    /// <summary>Whether the query contains any CJK at all, which is what the domain and header choices ask.</summary>
    public static bool HasCjk(string? text) => (Detect(text) & QueryScripts.Cjk) != 0;
}

/// <summary>What the first result of a batch is: a document ranked against others, or a record looked up.</summary>
public enum ResultNature
{
    /// <summary>Documents ordered by a relevance score. Everything here answers "what pages are about this".</summary>
    Ranked,

    /// <summary>A record the publisher owns (weather, a rate, a version). Answers the question itself.</summary>
    Record,
}

/// <summary>Where an engine sits in a planned pool.</summary>
public enum EnginePoolRole
{
    /// <summary>Breadth. Asked for every query the script filter lets through.</summary>
    Base,

    /// <summary>Indexes one kind properly. Asked when the query is about that kind.</summary>
    Specialist,

    /// <summary>Only asked when the pool has thinned to fewer than two usable base engines.</summary>
    Reserve,
}

/// <summary>
/// How the date of a result was obtained. This is the honesty mechanism: <see cref="None"/> means "age unknown",
/// which is not the same as "recent", and only the two first-hand sources may be used to discard a result.
/// </summary>
public enum DateSource
{
    /// <summary>No date at all. Shown as 日期不详, never filtered on.</summary>
    None,

    /// <summary>A field the interface returned (RFC 3339, Atom, an API's own timestamp).</summary>
    Structured,

    /// <summary>An explicit relative phrase the engine's own markup carried ("3 天前").</summary>
    Relative,

    /// <summary>An absolute date scraped out of prose. Display only — "成立于 1998 年" is not a publication date.</summary>
    Text,
}

/// <summary>The <c>kind</c> values <c>web_search</c> accepts, in the order the tool description offers them.</summary>
public static class SearchKinds
{
    public const string General = "general";
    public const string News = "news";
    public const string Academic = "academic";
    public const string Code = "code";
    public const string Encyclopedia = "encyclopedia";

    public static IReadOnlyList<string> Values { get; } = [General, News, Academic, Code, Encyclopedia];

    public static bool IsKnown(string? value) =>
        value is not null && Values.Contains(value, StringComparer.Ordinal);

    public static string Normalize(string? value) =>
        IsKnown(value) ? value! : General;
}

/// <summary>The <c>recency</c> values, which are also cache lifetimes; see <see cref="SearchCache"/>.</summary>
public static class SearchRecency
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string Year = "year";

    public static IReadOnlyList<string> Values { get; } = [Day, Week, Month, Year];

    public static bool IsKnown(string? value) =>
        value is not null && Values.Contains(value, StringComparer.Ordinal);

    /// <summary>The oldest publication date a result may carry and still be kept, or null for "no filter".</summary>
    public static DateTimeOffset? Cutoff(string? recency, DateTimeOffset now) => recency switch
    {
        Day => now.AddDays(-1),
        Week => now.AddDays(-7),
        Month => now.AddDays(-31),
        Year => now.AddDays(-366),
        _ => null,
    };
}

/// <summary>One search, as the caller asked for it. Immutable, so it is also the cache key's source.</summary>
/// <param name="Text">The query as written. Goes through <see cref="Uri.EscapeDataString"/> and nowhere else.</param>
/// <param name="Kind">One of <see cref="SearchKinds"/>; already normalized.</param>
/// <param name="Recency">One of <see cref="SearchRecency"/>, or null.</param>
/// <param name="Limit">How many results to ask each engine for. The merged answer is capped separately.</param>
public sealed record SearchQuery(string Text, string Kind = SearchKinds.General, string? Recency = null, int Limit = 8)
{
    /// <summary>
    /// Identity for the cache. The limit is in here because a cached eight-result answer is not an answer to a
    /// request for twenty.
    /// </summary>
    public string CacheKey => string.Join('\u0000', Text.Trim(), Kind, Recency ?? "-", Limit.ToString());

    /// <summary>The words of the query, in order, with punctuation dropped. What the decoy check counts against.</summary>
    public IReadOnlyList<string> Words { get; } = Text
        .Split([' ', '\t', '\n', '\r', '，', '。', '、', '？', '！', '；', '：', ',', '.', '?', '!', ';', ':', '(', ')', '（', '）', '"', '\'', '“', '”'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// One result. <see cref="Published"/> together with <see cref="DateSource"/> is deliberately two fields: a date
/// whose origin is unknown is worse than no date, because it cannot be caveated.
/// </summary>
public sealed record SearchHit(
    string Title,
    string Url,
    string Snippet,
    DateTimeOffset? Published,
    DateSource DateSource,
    string Engine,
    int Rank)
{
    /// <summary>
    /// Every engine that returned this same page. Set by <see cref="Merger"/> and empty until then.
    /// <para>
    /// This is the one piece of cross-engine evidence that survives into the answer, and the prompt asks the
    /// model to use it: two independent engines agreeing is worth more than one, and one source alone has to be
    /// reported as one source.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
}

/// <summary>
/// One engine's self-description. Declared next to the implementation so the two cannot drift, and read by the
/// planner so no hand-written routing table exists to go stale.
/// </summary>
/// <param name="Id">Stable key used in settings, health state and the "who answered" list.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Kinds">What it indexes. <see cref="ContentKinds.None"/> means "ask nothing of it without a routing rule".</param>
/// <param name="Scripts">Which query scripts it can answer.</param>
/// <param name="Handles">
/// Offline, pure, no network: whether this engine can answer the question outright (a place plus a weather word).
/// Null when the engine has no such form — which is most of them.
/// </param>
/// <param name="Nature">Whether it ranks documents or looks up a record.</param>
/// <param name="SingleSite">
/// A catalogue of one site (Wikipedia, arXiv). These never echo the query phrasing in their results, so the
/// decoy check must not run against them.
/// </param>
/// <param name="ExemptFromHostFilter">
/// A specialist must not be filtered by hostname rules written for general engines, or it would lose its own
/// legitimate links (arXiv results *are* arxiv.org).
/// </param>
/// <param name="Rate">What it will tolerate. A pool of one means a throttled engine is skipped, never queued.</param>
/// <param name="Role">Where it sits in a planned pool.</param>
/// <param name="NeedsCredentials">
/// Whether the source requires an account or an API key. Nothing in this build does — the field exists so that
/// the first engine that does (a self-hosted SearXNG is planned) cannot be swept into the automatic pool by
/// accident. See <see cref="QueryPlanner"/>, which refuses to route to one, and the test that keeps the set
/// credential-free.
/// </param>
public sealed record EngineTraits(
    string Id,
    string Name,
    ContentKinds Kinds,
    QueryScripts Scripts,
    Func<string, bool>? Handles,
    ResultNature Nature,
    bool SingleSite,
    bool ExemptFromHostFilter,
    RateLimit Rate,
    EnginePoolRole Role,
    bool NeedsCredentials = false);

/// <summary>
/// A token budget. <see cref="QueueFor"/> of zero is the interesting value: it means "if I cannot ask now, skip
/// me" — the fanout is parallel, and waiting on one throttled bucket would add its latency to everyone's answer.
/// </summary>
/// <param name="PerMinute">Tokens available per minute. Zero means unlimited.</param>
/// <param name="QueueFor">How long the engine is willing to wait for a token.</param>
public sealed record RateLimit(int PerMinute, TimeSpan QueueFor)
{
    /// <summary>No declared limit.</summary>
    public static RateLimit Unlimited { get; } = new(0, TimeSpan.Zero);

    /// <summary>Never queue: ask now or not at all.</summary>
    public static RateLimit Skip(TimeSpan queueFor) => new(0, queueFor);
}

/// <summary>What parsing a response produced, before anyone has judged whether it is any good.</summary>
public enum ParseStatus
{
    /// <summary>Results were extracted. Whether they are useful is <see cref="BatchGate"/>'s question.</summary>
    Ok,

    /// <summary>Parsed cleanly and legitimately found nothing.</summary>
    Empty,

    /// <summary>The page is a captcha, a consent wall, a login page or an empty script shell.</summary>
    Blocked,

    /// <summary>The markup no longer looks like what the parser was written for.</summary>
    Unparseable,
}

/// <summary>Parsing output. A parse failure is never an exception: it is a category the pipeline reports.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Hits">The results, empty unless <see cref="ParseStatus.Ok"/>.</param>
/// <param name="Note">A short human-readable detail for the "who did not answer" list.</param>
public sealed record ParseResult(ParseStatus Status, IReadOnlyList<SearchHit> Hits, string? Note = null)
{
    public static ParseResult Ok(IReadOnlyList<SearchHit> hits) => new(ParseStatus.Ok, hits);

    public static ParseResult Empty(string? note = null) => new(ParseStatus.Empty, [], note);

    public static ParseResult Blocked(string? note = null) => new(ParseStatus.Blocked, [], note);

    public static ParseResult Unparseable(string? note = null) => new(ParseStatus.Unparseable, [], note);
}
